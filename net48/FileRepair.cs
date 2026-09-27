using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Contra
{
    /// <summary>
    ///     Installation bootstrap and repair, driven by two sources:
    ///
    ///     1. Base-game content (BuiltinFileLists, embedded in the exe): ZH_GENERALS and ZH
    ///        files are restored from the registry-located retail/Steam installs (hard link
    ///        when the source sits on the same volume, copy otherwise). Existence-based -
    ///        a local retail install cannot provide a specific version.
    ///
    ///     2. The online index https://dl.mayeamiya.dev/index.html: every download link in
    ///        it belongs to a category folder (GeneralsOnlineUnlimited = engine + official
    ///        GO clients, ContraXBeta2Patch1 = mod, GenTool_v8.9 = GenTool). Missing files
    ///        are downloaded; already-present files are version-checked against the index
    ///        via HTTP HEAD (ETag + Content-Length, cached in Contra_RemoteCache.txt) and
    ///        re-fetched when either changes - no local file list needed.
    ///
    ///     First-launch gating: without Contra_Installed.marker the launcher demands a
    ///     clean (empty) folder, pops a bilingual notice and exits otherwise. The marker is
    ///     only written once an install completes without failures, switching later starts
    ///     into check/repair mode.
    /// </summary>
    internal static class FileRepair
    {
        /// <summary>
        ///     Install marker: its presence marks the folder as a completed launcher-managed
        ///     install (bootstrap done), switching later starts into check/repair mode.
        /// </summary>
        internal const string MarkerFileName = "Contra_Installed.marker";

        /// <summary>
        ///     Remembers the ETag + size of every index file we downloaded, so a changed
        ///     upstream file (new version) is detected without any local manifest.
        /// </summary>
        internal const string RemoteCacheFileName = "Contra_RemoteCache.txt";

        internal const string RemoteIndexUrl = "https://dl.mayeamiya.dev/index.html";

        private class RepairResult
        {
            public string Path;
            public string Error;
        }

        private class RemoteEntry
        {
            public string Url;
            public string RelativePath; // path below the launcher directory
        }

        private static readonly Regex IndexLinkRegex =
            new Regex("href=\"(?<url>https://dl\\.mayeamiya\\.dev/(?<path>[^\"]+))\"", RegexOptions.IgnoreCase);

        /// <summary>
        ///     Runs the whole bootstrap/repair pass. Silent when everything is present and
        ///     current; a top-most summary dialog lists restored files and failures.
        ///     Never throws: repair is a convenience and must not block launcher startup.
        /// </summary>
        public static async Task RunAsync(MainForm owner)
        {
            List<RepairResult> repaired = new List<RepairResult>();
            List<RepairResult> failed = new List<RepairResult>();
            string baseDir = MainForm.ResolveLauncherExecutingPath();

            try
            {
                // First-launch bootstrap: without the marker the launcher demands a clean
                // (empty) folder - it must never bootstrap-install on top of an existing or
                // unrelated directory. A dirty folder pops a bilingual notice and exits;
                // once the install completes, the marker switches every later start into
                // check/repair mode.
                if (!File.Exists(Path.Combine(baseDir, MarkerFileName)))
                {
                    string notClean = FindFirstForeignItem(baseDir);
                    if (notClean != null)
                    {
                        MessageBox.Show(new Form { TopMost = true },
                            "首次安装要求启动器位于干净（空）的文件夹中。\n" +
                            "当前文件夹包含： " + notClean + "\n\n" +
                            "请将 Contra_Launcher_New.exe 移入空文件夹后重新运行。\n" +
                            "已安装的目录（含 Contra_Installed.marker）会自动进入检查修复模式。\n\n" +
                            "First install requires the launcher to sit in a clean (empty) folder.\n" +
                            "This folder contains: " + notClean + "\n" +
                            "Move Contra_Launcher_New.exe into an empty folder and run again.\n" +
                            "Installed folders (with Contra_Installed.marker) switch to check/repair mode automatically.",
                            "Contra Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Application.Exit();
                        return;
                    }
                }

                List<string> zhInstalls = InstallLocator.FindZeroHourInstalls();
                List<string> generalsInstalls = InstallLocator.FindGeneralsInstalls();

                // Phase 1: base-game content from local installs (hard link / copy).
                RestoreFromInstalls(baseDir, BuiltinFileLists.ZhGeneralsFiles, generalsInstalls, "Generals", repaired, failed);
                RestoreFromInstalls(baseDir, BuiltinFileLists.ZeroHourFiles, zhInstalls, "Zero Hour", repaired, failed);

                // Phase 2: online content (engine, mod, GenTool) from the R2 index.
                List<RemoteEntry> remote = await LoadRemoteIndex();
                if (remote.Count > 0)
                {
                    Dictionary<string, string> cache = LoadRemoteCache(baseDir);
                    bool cacheDirty = false;

                    foreach (RemoteEntry entry in remote)
                    {
                        try
                        {
                            string target = Path.Combine(baseDir, entry.RelativePath.Replace('/', '\\'));
                            string cacheKey = entry.Url;

                            if (File.Exists(target))
                            {
                                string[] current = await HeadRemote(entry.Url);
                                if (current == null)
                                    continue; // index unreachable for this file; keep what we have

                                string cached;
                                cache.TryGetValue(cacheKey, out cached);

                                if (cached != null && cached == current[0] + "\t" + current[1])
                                    continue; // present and still the same version

                                if (cached == null)
                                {
                                    // Pre-existing file with no download history: accept it and
                                    // start tracking from here on.
                                    cache[cacheKey] = current[0] + "\t" + current[1];
                                    cacheDirty = true;
                                    continue;
                                }

                                // Cached version differs from the index: outdated, re-fetch below.
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            await owner.DownloadFile(entry.Url, target, TimeSpan.FromMinutes(30), CancellationToken.None);

                            string[] after = await HeadRemote(entry.Url);
                            if (after != null)
                            {
                                cache[cacheKey] = after[0] + "\t" + after[1];
                                cacheDirty = true;
                            }

                            repaired.Add(new RepairResult { Path = entry.RelativePath });
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new RepairResult { Path = entry.RelativePath, Error = ex.Message });
                        }
                    }

                    if (cacheDirty)
                        SaveRemoteCache(baseDir, cache);
                }

                // The marker is only written once a bootstrap completed without failures; a
                // failed install retries the whole bootstrap on the next start.
                if (!File.Exists(Path.Combine(baseDir, MarkerFileName)) && failed.Count == 0)
                {
                    try { File.WriteAllText(Path.Combine(baseDir, MarkerFileName), DateTime.Now.ToString("s")); }
                    catch { }
                }
            }
            catch
            {
                // A broken index or IO hiccup must never keep the launcher from starting.
            }

            if (repaired.Count > 0 || failed.Count > 0)
                ShowReport(repaired, failed);
        }

        // ---------------------------------------------------------------------
        // Phase 1: base-game content from registry-located local installs
        // ---------------------------------------------------------------------

        private static void RestoreFromInstalls(string baseDir, string[] files, List<string> installs,
            string gameName, List<RepairResult> repaired, List<RepairResult> failed)
        {
            foreach (string relativePath in files)
            {
                try
                {
                    string target = Path.Combine(baseDir, relativePath.Replace('/', '\\'));
                    if (File.Exists(target))
                        continue;

                    string error;
                    if (TryRestoreFromInstalls(relativePath, target, installs, gameName, out error))
                        repaired.Add(new RepairResult { Path = relativePath });
                    else
                        failed.Add(new RepairResult { Path = relativePath, Error = error });
                }
                catch (Exception ex)
                {
                    failed.Add(new RepairResult { Path = relativePath, Error = ex.Message });
                }
            }
        }

        private static bool TryRestoreFromInstalls(string relativePath, string target, List<string> installs,
            string gameName, out string error)
        {
            if (installs.Count == 0)
            {
                error = "注册表中未找到 " + gameName + " 安装位置 / no " + gameName +
                        " install found in the registry";
                return false;
            }

            foreach (string install in installs)
            {
                string source = Path.Combine(install, relativePath.Replace('/', '\\'));
                if (!File.Exists(source))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (TryHardLink(source, target))
                {
                    error = null;
                    return true;
                }

                File.Copy(source, target, false);
                error = null;
                return true;
            }

            error = "注册表给出的 " + gameName + " 目录中也没有该文件 / not found in any registry-located " +
                    gameName + " install";
            return false;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        /// <summary>
        ///     Hard-links the file instead of copying when source and target sit on the same
        ///     volume: zero extra disk space and instant, while staying a normal file for the
        ///     game. Returns false when linking fails (different volume, FAT/exFAT, permissions)
        ///     so the caller falls back to File.Copy.
        /// </summary>
        private static bool TryHardLink(string source, string target)
        {
            try
            {
                string sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
                string targetRoot = Path.GetPathRoot(Path.GetFullPath(target));
                if (string.IsNullOrEmpty(sourceRoot) || !string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
                    return false;

                return CreateHardLink(target, source, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------------
        // Phase 2: online content from the R2 index
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Turns every download link of the index page into a remote entry: the folder
        ///     decides the category, everything below it is the path inside the launcher
        ///     directory (URL-decoded, so names like "Gentool Website.url" survive).
        /// </summary>
        private static async Task<List<RemoteEntry>> LoadRemoteIndex()
        {
            var entries = new List<RemoteEntry>();

            string html = await MainForm.httpclient.GetStringAsync(RemoteIndexUrl);
            foreach (Match match in IndexLinkRegex.Matches(html))
            {
                string url = match.Groups["url"].Value;
                string path = Uri.UnescapeDataString(match.Groups["path"].Value);

                int folderEnd = path.IndexOf('/');
                if (folderEnd <= 0)
                    continue; // e.g. the index itself

                string folder = path.Substring(0, folderEnd);
                string relative = path.Substring(folderEnd + 1);
                if (relative.Length == 0)
                    continue;

                if (folder != "GeneralsOnlineUnlimited" && folder != "ContraXBeta2Patch1" && folder != "GenTool_v8.9")
                    continue; // unknown category; ignored until it is part of the layout

                entries.Add(new RemoteEntry { Url = url, RelativePath = relative });
            }

            return entries;
        }

        /// <summary>
        ///     ETag + Content-Length of a remote file, or null when the HEAD fails. The pair
        ///     doubles as the version stamp: any upstream change moves at least one of them.
        /// </summary>
        private static async Task<string[]> HeadRemote(string url)
        {
            try
            {
                using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url))
                using (var response = await MainForm.httpclient.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;

                    string etag = response.Headers.ETag != null ? response.Headers.ETag.Tag : "";
                    long size = response.Content.Headers.ContentLength.GetValueOrDefault(-1);
                    return new[] { etag ?? "", size.ToString() };
                }
            }
            catch
            {
                return null;
            }
        }

        private static string RemoteCachePath(string baseDir)
        {
            return Path.Combine(baseDir, RemoteCacheFileName);
        }

        private static Dictionary<string, string> LoadRemoteCache(string baseDir)
        {
            var cache = new Dictionary<string, string>();
            try
            {
                foreach (string line in File.ReadAllLines(RemoteCachePath(baseDir)))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length == 3 && parts[0].Length > 0)
                        cache[parts[0]] = parts[1] + "\t" + parts[2];
                }
            }
            catch
            {
                // Missing or broken cache simply means "nothing known yet".
            }
            return cache;
        }

        private static void SaveRemoteCache(string baseDir, Dictionary<string, string> cache)
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> pair in cache)
                {
                    string[] half = pair.Value.Split('\t');
                    lines.Add(pair.Key + "\t" + half[0] + "\t" + half[1]);
                }
                File.WriteAllLines(RemoteCachePath(baseDir), lines);
            }
            catch
            {
                // A cache write failure only costs a re-download on the next start.
            }
        }

        // ---------------------------------------------------------------------
        // First-launch clean-folder gate
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Returns the first file/subdirectory in the launcher folder that does not belong
        ///     to the launcher itself, or null when the folder is clean (empty). runtime_lib\
        ///     (locally bundled runtime installers) is allowed alongside the launcher.
        /// </summary>
        private static string FindFirstForeignItem(string baseDir)
        {
            string[] ownFiles = { MarkerFileName, "Contra_Launcher_New.exe", "Contra_Launcher_New.pdb", RemoteCacheFileName };

            foreach (string file in Directory.GetFiles(baseDir))
            {
                string name = Path.GetFileName(file);
                bool own = false;
                foreach (string ownName in ownFiles)
                    if (string.Equals(name, ownName, StringComparison.OrdinalIgnoreCase)) { own = true; break; }
                if (!own)
                    return name;
            }

            foreach (string dir in Directory.GetDirectories(baseDir))
                if (!string.Equals(Path.GetFileName(dir), "runtime_lib", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dir) + @"\";

            return null;
        }

        // ---------------------------------------------------------------------
        // Report
        // ---------------------------------------------------------------------

        private static void ShowReport(List<RepairResult> repaired, List<RepairResult> failed)
        {
            string text = "";

            if (repaired.Count > 0)
            {
                text += "已修复缺失文件 (Missing files were restored):\n\n";
                foreach (RepairResult result in repaired)
                    text += "  [+] " + result.Path + "\n";
                text += "\n";
            }

            if (failed.Count > 0)
            {
                text += "以下缺失文件无法自动修复 (These missing files could not be restored):\n\n";
                foreach (RepairResult result in failed)
                    text += "  [x] " + result.Path + "\n      " + result.Error + "\n";
                text += "\n请检查注册表中的游戏安装或网络连接。(Check your game install registration or network connection.)";
            }

            try
            {
                MessageBox.Show(new Form { TopMost = true }, text, "Contra Launcher",
                    MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch
            {
                // The report is informational; nothing should crash over showing it.
            }
        }
    }
}
