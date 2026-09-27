using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Contra
{
    /// <summary>
    ///     Installation bootstrap and repair, driven by two sources:
    ///
    ///     1. Base-game content by WHOLESALE DIRECTORY MAPPING (no per-file lists): the
    ///        Steam-first Zero Hour install maps onto the launcher root (top-level files
    ///        hard linked, top-level directories junctioned) - a Steam install brings its
    ///        own ZH_Generals along; a retail Generals install maps into ZH_Generals.
    ///
    ///     2. The online index https://dl.mayeamiya.dev/index.html: every download link in
    ///        it belongs to a category folder (GeneralsOnlineUnlimited = engine + official
    ///        GO clients, ContraXBeta2Patch1 = mod, GenTool_v8.9 = GenTool). The check pass
    ///        computes the download list upfront; the progress window counts downloads only.
    ///
    ///     First-launch gating: without Contra_Installed.marker the launcher demands a
    ///     clean (empty) folder, pops a notice and exits otherwise. The marker is written
    ///     after the first pass unconditionally, switching later starts into check/repair
    ///     mode; a pass that changed anything restarts the launcher immediately.
    /// </summary>
    internal static class FileRepair
    {
        /// <summary>
        ///     Install marker: its presence marks the folder as a completed launcher-managed
        ///     install (bootstrap done), switching later starts into check/repair mode.
        /// </summary>
        internal const string MarkerFileName = "Contra_Installed.marker";

        /// <summary>
        ///     True when the launcher directory carries the install marker, i.e. the first
        ///     bootstrap already ran. Gates one-time behaviours such as language detection.
        /// </summary>
        public static bool MarkerExists()
        {
            return File.Exists(Path.Combine(MainForm.ResolveLauncherExecutingPath(), MarkerFileName));
        }

        /// <summary>
        ///     Remembers the ETag + size of every index file we downloaded, so a changed
        ///     upstream file (new version) is detected without any local manifest.
        /// </summary>
        internal const string RemoteCacheFileName = "Contra_RemoteCache.txt";

        internal const string RemoteIndexUrl = "https://dl.mayeamiya.dev/index.html";

        /// <summary>The official Contra broadcast (bottom marquee) source, upstream repository.</summary>
        internal const string OfficialMOTDUrl = "https://raw.githubusercontent.com/ContraMod/Launcher/master/Versions_X.txt";

        /// <summary>Single-language text picker: everything the user sees follows the launcher language.</summary>
        private static string L(string cn, string en)
        {
            return Globals.currentLanguage == "CN" ? cn : en;
        }

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
        ///     First-launch gate, run before everything else (including the self-update):
        ///     without Contra_Installed.marker the launcher demands a clean (empty) folder -
        ///     it must never bootstrap-install on top of an existing or unrelated directory.
        ///     A dirty folder pops a bilingual notice and exits the launcher. Returns true
        ///     when startup may continue (marker present, or folder is clean for bootstrap).
        /// </summary>
        public static bool EnsureCleanFolder()
        {
            string baseDir = MainForm.ResolveLauncherExecutingPath();
            if (File.Exists(Path.Combine(baseDir, MarkerFileName)))
                return true; // established install: check/repair mode

            string notClean = FindFirstForeignItem(baseDir);
            if (notClean == null)
                return true; // clean folder: the bootstrap will run in RunAsync

            MessageBox.Show(new Form { TopMost = true },
                L("首次安装要求启动器位于干净（空）的文件夹中。\n当前文件夹包含： ",
                  "First install requires the launcher to sit in a clean (empty) folder.\nThis folder contains: ")
                + notClean + "\n\n" +
                L("请将 Contra_Launcher_New.exe 移入空文件夹后重新运行。\n已安装的目录（含 Contra_Installed.marker）会自动进入检查修复模式。",
                  "Move Contra_Launcher_New.exe into an empty folder and run again.\nInstalled folders (with Contra_Installed.marker) switch to check/repair mode automatically."),
                "Contra Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Application.Exit();
            return false;
        }

        /// <summary>
        ///     Runs the whole bootstrap/repair pass. Local restores happen silently (they are
        ///     hard links / copies and finish instantly); the progress window only appears
        ///     when files actually have to download, and its total counts exactly the files
        ///     that need downloading - computed upfront by the check pass, nothing else.
        ///     Never throws: repair is a convenience and must not block launcher startup.
        /// </summary>
        public static async Task RunAsync(MainForm owner)
        {
            List<RepairResult> repaired = new List<RepairResult>();
            List<RepairResult> failed = new List<RepairResult>();
            string baseDir = MainForm.ResolveLauncherExecutingPath();
            bool cancelled = false;

            try
            {
                List<string> zhInstalls = InstallLocator.FindZeroHourInstalls();
                List<string> generalsInstalls = InstallLocator.FindGeneralsInstalls();

                // Steam depots are the preferred restore source (complete, Steam-verified
                // layouts); retail installs come after them.
                zhInstalls = SortSteamsFirst(zhInstalls);
                generalsInstalls = SortSteamsFirst(generalsInstalls);

                // Phase 1: base-game content from the local installs. These are hard links /
                // copies and finish instantly - no progress UI, no counters for them.
                RestoreBaseGame(baseDir, zhInstalls, generalsInstalls, repaired, failed);

                // Phase 2a: check the R2 index and work out exactly what has to download.
                List<RemoteEntry> remote = await LoadRemoteIndex();
                var downloads = new List<KeyValuePair<RemoteEntry, long>>();
                foreach (RemoteEntry entry in remote)
                {
                    string target = Path.Combine(baseDir, entry.RelativePath.Replace('/', '\\'));

                    // Mod files toggle between .ctr (inactive) and .big (activated by the
                    // option renames on every launch), so presence is matched on the file
                    // NAME with either extension - matching the exact .ctr name only would
                    // re-download every activated file on each start.
                    if (File.Exists(target) || File.Exists(AlternateCtrBigPath(target)))
                        continue;

                    // Special case: the two music override packs (MusicEnhanced /
                    // MusicTheScore) get an extra "!" prefix on start - they must load
                    // before the base audio to override it. Presence accepts the fixed
                    // double-! name too, or the pass would re-download them every start.
                    bool isMusicOverride = entry.RelativePath.IndexOf("_MusicEnhanced.", StringComparison.OrdinalIgnoreCase) >= 0
                        || entry.RelativePath.IndexOf("_MusicTheScore.", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isMusicOverride && entry.RelativePath.StartsWith("!"))
                    {
                        string fixedTarget = "!" + target; // single -> double "!"
                        if (File.Exists(fixedTarget) || File.Exists(AlternateCtrBigPath(fixedTarget)))
                            continue;
                    }

                    string[] head = await HeadRemote(entry.Url);
                    downloads.Add(new KeyValuePair<RemoteEntry, long>(entry, head != null ? long.Parse(head[1]) : 0));
                }

                if (downloads.Count == 0)
                {
                    WriteMarkerIfNew(baseDir);

                    // Only local restores happened, but the directory still changed.
                    if (repaired.Count > 0 && failed.Count == 0)
                        Application.Restart();
                    return;
                }

                // Phase 2b: download exactly the missing files. Progress window on top, main
                // form disabled until the pass is done. The total counts downloads only.
                long totalBytes = 0;
                foreach (KeyValuePair<RemoteEntry, long> download in downloads)
                    totalBytes += download.Value;

                FileRepairProgressForm progress = new FileRepairProgressForm(downloads.Count);
                progress.Show(owner);
                owner.Enabled = false;
                progress.FormClosed += (sender, args) => owner.Enabled = true;

                try
                {
                    progress.SetPhase(L("正在下载", "Downloading"));
                    long overallReceived = 0;
                    Stopwatch sessionWatch = Stopwatch.StartNew();
                    double lastCallbackSeconds = 0;
                    long lastCallbackBytes = 0;
                    double smoothedSpeed = 0;
                    int done = 0;

                    foreach (KeyValuePair<RemoteEntry, long> download in downloads)
                    {
                        if (progress.Cancellation.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        RemoteEntry entry = download.Key;
                        string target = Path.Combine(baseDir, entry.RelativePath.Replace('/', '\\'));
                        long fileStartOverall = overallReceived;

                        try
                        {
                            progress.SetFile(entry.RelativePath, 0);
                            progress.ClearStats();

                            Directory.CreateDirectory(Path.GetDirectoryName(target));

                            await owner.DownloadFile(entry.Url, target, TimeSpan.FromMinutes(30),
                                progress.Cancellation.Token, (received, total) =>
                                {
                                    overallReceived = fileStartOverall + received;

                                    int percent = total > 0 ? (int)Math.Min(100, received * 100 / total) : 0;
                                    progress.SetFile(entry.RelativePath, percent);

                                    // ONE shared session speed drives both rows (per-file ETA and
                                    // overall ETA), smoothed so the two readouts always agree.
                                    double elapsed = sessionWatch.Elapsed.TotalSeconds;
                                    double dt = elapsed - lastCallbackSeconds;
                                    long dBytes = overallReceived - lastCallbackBytes;
                                    if (dt > 0.05 && dBytes > 0)
                                    {
                                        double instant = dBytes / dt;
                                        smoothedSpeed = smoothedSpeed <= 0 ? instant : smoothedSpeed * 0.7 + instant * 0.3;
                                        lastCallbackSeconds = elapsed;
                                        lastCallbackBytes = overallReceived;
                                    }

                                    string eta = smoothedSpeed > 1 ? FormatDuration((total - received) / smoothedSpeed) : "--:--";
                                    progress.SetStats(received, total, smoothedSpeed, eta);

                                    long overallRemaining = Math.Max(0, totalBytes - overallReceived);
                                    progress.SetOverallStats(overallReceived, totalBytes, smoothedSpeed,
                                        smoothedSpeed > 1 ? FormatDuration(overallRemaining / smoothedSpeed) : "--:--");
                                });

                            repaired.Add(new RepairResult { Path = entry.RelativePath });
                        }
                        catch (OperationCanceledException)
                        {
                            // A cancelled download leaves a partial file behind; remove it so
                            // the next start re-downloads from scratch.
                            try { if (File.Exists(target)) File.Delete(target); } catch { }
                            cancelled = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new RepairResult { Path = entry.RelativePath, Error = ex.Message });
                        }
                        finally
                        {
                            done++;
                            progress.SetOverall(done);
                        }
                    }

                    if (!cancelled)
                    {
                        progress.ClearOverallStats();
                        progress.SetFile("", 100);
                    }

                    // The marker is written after the very first pass no matter what: even
                    // with failures or a cancellation the folder is now launcher-managed and
                    // every later start runs in check/repair mode.
                    WriteMarkerIfNew(baseDir);

                    // The sync changed the directory: restart the launcher right away so every
                    // piece of UI re-reads the freshly restored files. Failures keep the
                    // launcher open for the report instead (avoids failure-restart loops).
                    if (!cancelled && repaired.Count > 0 && failed.Count == 0)
                    {
                        Application.Restart();
                        return;
                    }
                }
                finally
                {
                    progress.Close();
                    progress.Dispose();
                }
            }
            catch
            {
                // A broken index or IO hiccup must never keep the launcher from starting.
            }

            if (failed.Count > 0)
                ShowFailures(failed);
        }

        /// <summary>Orders install candidates Steam-first (steamapps paths), keeping the rest.</summary>
        private static List<string> SortSteamsFirst(List<string> installs)
        {
            return installs
                .OrderByDescending(install => IsSteamInstall(install))
                .ToList();
        }

        /// <summary>Writes the install marker when it is not there yet (idempotent).</summary>
        private static void WriteMarkerIfNew(string baseDir)
        {
            if (!File.Exists(Path.Combine(baseDir, MarkerFileName)))
            {
                try { File.WriteAllText(Path.Combine(baseDir, MarkerFileName), DateTime.Now.ToString("s")); }
                catch { }
            }
        }

        private static string FormatDuration(double seconds)
        {
            TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
            if (span.TotalHours >= 1)
                return ((int)span.TotalHours).ToString("00") + ":" + span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00");
            return span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00");
        }

        // ---------------------------------------------------------------------
        // ---------------------------------------------------------------------
        // Phase 1: base-game content by WHOLESALE DIRECTORY MAPPING (no per-file
        // lists): the mapped install's top-level files are hard linked (same
        // volume) or copied, its top-level directories are junctioned. Whatever
        // the local install carries - including its language packs - comes along
        // under its own name. Different drive falls back to copying.
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Base-game restore, Steam-first: the whole Steam Zero Hour directory maps
        ///     onto the launcher root (its ZH_Generals subfolder comes along inside);
        ///     retail installs map Zero Hour onto the launcher root and Generals into
        ///     ZH_Generals.
        /// </summary>
        private static void RestoreBaseGame(string baseDir, List<string> zhInstalls,
            List<string> generalsInstalls, List<RepairResult> repaired, List<RepairResult> failed)
        {
            if (zhInstalls.Count > 0)
            {
                MapInstallToTarget(zhInstalls[0], baseDir, repaired, failed);

                if (IsSteamInstall(zhInstalls[0]))
                    RestoreSteamExtras(baseDir, zhInstalls, repaired, failed);
            }

            // Steam installs carry ZH_Generals inside; retail ones do not, so the
            // Generals install maps into ZH_Generals when nothing provided it yet.
            if (!Directory.Exists(Path.Combine(baseDir, "ZH_Generals")) && generalsInstalls.Count > 0)
                MapInstallToTarget(generalsInstalls[0], Path.Combine(baseDir, "ZH_Generals"), repaired, failed);
        }

        /// <summary>
        ///     Maps an install directory into a target directory: top-level files are hard
        ///     linked (same volume) or copied, top-level directories are junctioned. Items
        ///     that already exist are left untouched; when a target subdirectory already
        ///     exists as a real folder its contents are mapped recursively instead.
        /// </summary>
        private static void MapInstallToTarget(string install, string targetBase,
            List<RepairResult> repaired, List<RepairResult> failed)
        {
            try
            {
                Directory.CreateDirectory(targetBase);

                foreach (string file in Directory.GetFiles(install))
                {
                    string target = Path.Combine(targetBase, Path.GetFileName(file));
                    if (File.Exists(target))
                        continue;

                    if (TryHardLink(file, target))
                        repaired.Add(new RepairResult { Path = Path.GetFileName(file) + " (link)" });
                    else
                    {
                        File.Copy(file, target, false);
                        repaired.Add(new RepairResult { Path = Path.GetFileName(file) + " (copy)" });
                    }
                }

                foreach (string dir in Directory.GetDirectories(install))
                {
                    string name = Path.GetFileName(dir);
                    string target = Path.Combine(targetBase, name);

                    if (Directory.Exists(target))
                    {
                        if (IsReparsePoint(target))
                            continue; // already mapped
                        MapInstallToTarget(dir, target, repaired, failed); // real folder: map into it
                        continue;
                    }

                    if (TryCreateJunction(target, dir))
                        repaired.Add(new RepairResult { Path = name + " -> " + dir + " (junction)" });
                    else
                        MapInstallToTarget(dir, target, repaired, failed); // junction refused: map per item
                }
            }
            catch (Exception ex)
            {
                failed.Add(new RepairResult { Path = install, Error = ex.Message });
            }
        }

        /// <summary>Steam depots live under .../steamapps/...; those map, retail does not.</summary>
        private static bool IsSteamInstall(string installPath)
        {
            return installPath != null && installPath.ToLowerInvariant().Contains("steamapps");
        }

        /// <summary>
        ///     Steam installs carry the DRM stubs next to the game exe; when restoring from a
        ///     Steam install they are mapped into the launcher directory as well (hard link
        ///     first, copy fallback). Not part of the progress total - they are a side effect.
        /// </summary>
        private static void RestoreSteamExtras(string baseDir, List<string> zhInstalls,
            List<RepairResult> repaired, List<RepairResult> failed)
        {
            string[] steamExtras = { "steam_api.dll", "steam_appid.txt" };

            foreach (string fileName in steamExtras)
            {
                try
                {
                    string target = Path.Combine(baseDir, fileName);
                    if (File.Exists(target))
                        continue;

                    foreach (string install in zhInstalls)
                    {
                        if (!IsSteamInstall(install))
                            continue;

                        string source = Path.Combine(install, fileName);
                        if (!File.Exists(source))
                            continue;

                        if (TryHardLink(source, target))
                            repaired.Add(new RepairResult { Path = fileName + " (steam link)" });
                        else
                        {
                            File.Copy(source, target, false);
                            repaired.Add(new RepairResult { Path = fileName + " (steam copy)" });
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(new RepairResult { Path = fileName, Error = ex.Message });
                }
            }
        }

        /// <summary>True when the directory is a junction/symlink rather than a real folder.</summary>
        private static bool IsReparsePoint(string path)
        {
            try
            {
                return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        ///     Creates a directory junction (no admin rights needed) via mklink /J. Returns
        ///     false when the shell refuses, so the caller can fall back to per-file links.
        /// </summary>
        private static bool TryCreateJunction(string junctionPath, string targetDir)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c mklink /J \"" + junctionPath + "\" \"" + targetDir + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                using (Process process = Process.Start(psi))
                {
                    if (process != null)
                        process.WaitForExit();
                    return Directory.Exists(junctionPath);
                }
            }
            catch
            {
                return false;
            }
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
        ///     Returns the same path with the .ctr/.big extension swapped (the launcher's
        ///     option system renames mod files between the two); paths without either
        ///     extension are returned unchanged.
        /// </summary>
        private static string AlternateCtrBigPath(string path)
        {
            if (path.EndsWith(".ctr", StringComparison.OrdinalIgnoreCase))
                return path.Substring(0, path.Length - 4) + ".big";
            if (path.EndsWith(".big", StringComparison.OrdinalIgnoreCase))
                return path.Substring(0, path.Length - 4) + ".ctr";
            return path;
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
        // Failure report (success only shows in the progress window - no big dialog)
        // ---------------------------------------------------------------------

        private static void ShowFailures(List<RepairResult> failed)
        {
            string text = L("以下缺失文件无法自动修复：\n\n",
                            "These missing files could not be restored:\n\n");

            int shown = 0;
            foreach (RepairResult result in failed)
            {
                if (shown++ >= 15)
                {
                    text += "  ... 共 / total " + failed.Count + " 个\n";
                    break;
                }
                text += "  [x] " + result.Path + "\n      " + result.Error + "\n";
            }

            text += "\n" + L("请检查注册表中的游戏安装或网络连接。",
                             "Check your game install registration or network connection.");

            try
            {
                MessageBox.Show(new Form { TopMost = true }, text, "Contra Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch
            {
                // The report is informational; nothing should crash over showing it.
            }
        }
    }
}
