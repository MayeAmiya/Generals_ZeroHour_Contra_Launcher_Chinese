using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Contra
{
    /// <summary>
    ///     Manifest-driven installation repair. Contra_FileList.txt (next to the launcher exe)
    ///     lists every file the distribution expects, one per line as
    ///     "CATEGORY|relative\path[|sha256]"; blank lines and lines starting with "#" are
    ///     comments. Categories decide how a missing file is restored:
    ///
    ///       ZH_GENERALS - base Generals content; copied from the registry-located Generals
    ///                     install (retail EA App, Steam, or The First Decade)
    ///       ZH          - Zero Hour content; copied from the registry-located Zero Hour
    ///                     install; an error is reported when the file exists in neither
    ///       ENGINE      - our compiled GeneralsOnline engine build; downloaded from R2
    ///       MOD         - Contra mod content; downloaded from R2
    ///
    ///     The optional third field carries the expected SHA-256 of the file. When present
    ///     (MOD and ENGINE) the file is hash-checked on every start: a missing OR outdated
    ///     (hash mismatch = older version) file is fetched again from R2 and verified after
    ///     download, so manifest hash bumps double as version bumps. ZH categories repair by
    ///     existence only - a local retail install cannot provide a specific version.
    ///
    ///     The launcher executable itself is version-updated from R2 through MainForm's
    ///     UpdateLogic. A missing list file keeps the whole feature dormant, so the launcher
    ///     stays usable with a plain manual install until the final list ships.
    /// </summary>
    internal static class FileRepair
    {
        internal const string ManifestFileName = "Contra_FileList.txt";

        /// <summary>
        ///     Install marker: its presence marks the folder as a completed launcher-managed
        ///     install (bootstrap done), switching later starts into check/repair mode.
        /// </summary>
        internal const string MarkerFileName = "Contra_Installed.marker";

        private class Entry
        {
            public string Category;
            public string RelativePath; // backslash separated, exactly as written in the manifest
            public string Sha256;       // optional expected digest (lowercase hex, 64 chars)
            public string Error;        // human readable reason when restore failed
        }

        /// <summary>
        ///     Checks every manifest entry against the launcher directory and restores what is
        ///     missing. Silent when everything is present or the manifest does not exist; a
        ///     top-most summary dialog lists restored files and unfixable failures otherwise.
        ///     Never throws: repair is a convenience and must not block launcher startup.
        /// </summary>
        public static async Task RunAsync()
        {
            List<Entry> repaired = new List<Entry>();
            List<Entry> failed = new List<Entry>();

            try
            {
                string baseDir = MainForm.ResolveLauncherExecutingPath();
                string manifestPath = Path.Combine(baseDir, ManifestFileName);
                if (!File.Exists(manifestPath))
                    return; // feature dormant until we ship the final file list

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
                            "请将 Contra_Launcher.exe 与 Contra_FileList.txt 移入空文件夹后重新运行。\n" +
                            "已安装的目录（含 Contra_Installed.marker）会自动进入检查修复模式。\n\n" +
                            "First install requires the launcher to sit in a clean (empty) folder.\n" +
                            "This folder contains: " + notClean + "\n" +
                            "Move Contra_Launcher.exe and Contra_FileList.txt into an empty folder and run again.\n" +
                            "Installed folders (with Contra_Installed.marker) switch to check/repair mode automatically.",
                            "Contra Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Application.Exit();
                        return;
                    }
                }

                List<Entry> entries = ParseManifest(File.ReadAllText(manifestPath));
                if (entries.Count == 0)
                    return;

                List<string> zhInstalls = InstallLocator.FindZeroHourInstalls();
                List<string> generalsInstalls = InstallLocator.FindGeneralsInstalls();

                foreach (Entry entry in entries)
                {
                    try
                    {
                        string target = Path.Combine(baseDir, entry.RelativePath.Replace('/', '\\'));
                        if (File.Exists(target))
                        {
                            // Existence-only categories accept whatever is on disk. Hashed
                            // categories (MOD/ENGINE) verify every start so a version bump in
                            // the manifest re-fetches outdated files automatically.
                            if (entry.Sha256 == null || VerifySha256(target, entry.Sha256))
                                continue;
                        }

                        if (await RestoreEntry(entry, target, zhInstalls, generalsInstalls))
                            repaired.Add(entry);
                        else
                            failed.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        entry.Error = ex.Message;
                        failed.Add(entry);
                    }
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
                // A broken manifest or IO hiccup must never keep the launcher from starting.
            }

            if (repaired.Count > 0 || failed.Count > 0)
                ShowReport(repaired, failed);
        }

        /// <summary>
        ///     Returns the first file/subdirectory in the launcher folder that does not belong
        ///     to the launcher itself, or null when the folder is clean (empty).
        /// </summary>
        private static string FindFirstForeignItem(string baseDir)
        {
            string[] ownFiles = { ManifestFileName, MarkerFileName, "Contra_Launcher.exe", "Contra_Launcher.pdb" };

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
                return Path.GetFileName(dir) + @"\";

            return null;
        }

        private static List<Entry> ParseManifest(string content)
        {
            var entries = new List<Entry>();

            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;

                int separator = line.IndexOf('|');
                if (separator <= 0)
                    continue; // malformed line; skip instead of failing the whole pass

                Entry entry = new Entry();
                entry.Category = line.Substring(0, separator).Trim().ToUpperInvariant();
                entry.RelativePath = line.Substring(separator + 1).Trim();

                // Optional third field: expected SHA-256 (64 hex chars); anything else is ignored.
                int hashSeparator = entry.RelativePath.IndexOf('|');
                if (hashSeparator >= 0)
                {
                    entry.Sha256 = entry.RelativePath.Substring(hashSeparator + 1).Trim().ToLowerInvariant();
                    entry.RelativePath = entry.RelativePath.Substring(0, hashSeparator).Trim();
                    if (!IsSha256(entry.Sha256))
                        entry.Sha256 = null;
                }

                if (entry.RelativePath.Length == 0)
                    continue;

                entries.Add(entry);
            }

            return entries;
        }

        /// <returns>true when the file was restored, false when it is unrecoverable (Error set).</returns>
        private static async Task<bool> RestoreEntry(Entry entry, string target,
            List<string> zhInstalls, List<string> generalsInstalls)
        {
            switch (entry.Category)
            {
                case "ZH_GENERALS":
                    return CopyFromInstalls(entry, target, generalsInstalls, "Generals");

                case "ZH":
                    return CopyFromInstalls(entry, target, zhInstalls, "Zero Hour");

                case "ENGINE":
                case "MOD":
                    return await DownloadFromS3(entry, target);

                default:
                    entry.Error = "未知分类 / unknown category \"" + entry.Category + "\"";
                    return false;
            }
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
                return false;

            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                    return false;
            }
            return true;
        }

        /// <summary>
        ///     True when the file's SHA-256 matches the manifest's expected digest. Any read
        ///     failure counts as a mismatch so the caller restores the file.
        /// </summary>
        private static bool VerifySha256(string path, string expected)
        {
            try
            {
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    return string.Equals(actual, expected, StringComparison.Ordinal);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool CopyFromInstalls(Entry entry, string target, List<string> installs, string gameName)
        {
            if (installs.Count == 0)
            {
                entry.Error = "注册表中未找到 " + gameName + " 安装位置 / no " + gameName +
                              " install found in the registry";
                return false;
            }

            foreach (string install in installs)
            {
                string source = Path.Combine(install, entry.RelativePath.Replace('/', '\\'));
                if (!File.Exists(source))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (TryHardLink(source, target))
                    return true;

                File.Copy(source, target, false);
                return true;
            }

            entry.Error = "注册表给出的 " + gameName + " 目录中也没有该文件 / not found in any registry-located " +
                          gameName + " install";
            return false;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        /// <summary>
        ///     Hard-links the file instead of copying when source and target sit on the same
        ///     volume: zero extra disk space and instant, while staying a normal file for the
        ///     game. Falls back to File.Copy at the caller when linking fails (different
        ///     volume, FAT/exFAT, permissions).
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

        private static async Task<bool> DownloadFromS3(Entry entry, string target)
        {
            if (string.IsNullOrEmpty(MainForm.S3_BaseUrl))
            {
                entry.Error = "下载通道未配置 / the download channel is not configured yet";
                return false;
            }

            // Each category lives in its own folder on R2 (see dl.mayeamiya.dev):
            // engine files under GeneralsOnlineUnlimited/, mod files under ContraXBeta2Patch1/.
            string remoteFolder = entry.Category == "ENGINE" ? "GeneralsOnlineUnlimited/" : "ContraXBeta2Patch1/";

            // Backslashes become path segments; segments are escaped so spaces survive the URL.
            string urlPath = remoteFolder + entry.RelativePath.Replace('\\', '/');
            string[] segments = urlPath.Split('/');
            for (int i = 0; i < segments.Length; i++)
                segments[i] = Uri.EscapeUriString(segments[i]);
            string url = MainForm.S3_BaseUrl + string.Join("/", segments);

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            await MainForm.DownloadFileSimple(url, target, TimeSpan.FromMinutes(10));

            // The manifest hash doubles as the version stamp: a freshly downloaded file that
            // still mismatches means the R2 copy is not (yet) the manifest's version.
            if (entry.Sha256 != null && !VerifySha256(target, entry.Sha256))
            {
                entry.Error = "下载完成但哈希不符（R2 上的版本与清单不一致）/ downloaded but hash mismatch - " +
                              "the R2 copy does not match the manifest version";
                return false;
            }
            return true;
        }

        private static void ShowReport(List<Entry> repaired, List<Entry> failed)
        {
            string text = "";

            if (repaired.Count > 0)
            {
                text += "已修复缺失文件 (Missing files were restored):\n\n";
                foreach (Entry entry in repaired)
                    text += "  [+] " + entry.RelativePath + "\n";
                text += "\n";
            }

            if (failed.Count > 0)
            {
                text += "以下缺失文件无法自动修复 (These missing files could not be restored):\n\n";
                foreach (Entry entry in failed)
                    text += "  [x] " + entry.RelativePath + "\n      " + entry.Error + "\n";
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
