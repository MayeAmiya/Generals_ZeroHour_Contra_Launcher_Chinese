using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Contra
{
    /// <summary>
    ///     Manifest-driven installation repair. Contra_FileList.txt (next to the launcher exe)
    ///     lists every file the distribution expects, one per line as "CATEGORY|relative\path";
    ///     blank lines and lines starting with "#" are comments. Categories decide how a
    ///     missing file is restored:
    ///
    ///       ZH_GENERALS - base Generals content; copied from the registry-located Generals
    ///                     install (retail EA App, Steam, or The First Decade)
    ///       ZH          - Zero Hour content; copied from the registry-located Zero Hour
    ///                     install; an error is reported when the file exists in neither
    ///       ENGINE      - our compiled GeneralsOnline engine build; downloaded from S3
    ///       MOD         - Contra mod content; downloaded from S3
    ///
    ///     The launcher executable itself is version-updated from S3 through MainForm's
    ///     UpdateLogic. A missing list file keeps the whole feature dormant, so the launcher
    ///     stays usable with a plain manual install until the final list ships.
    /// </summary>
    internal static class FileRepair
    {
        internal const string ManifestFileName = "Contra_FileList.txt";

        private class Entry
        {
            public string Category;
            public string RelativePath; // backslash separated, exactly as written in the manifest
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
                string manifestPath = Path.Combine(MainForm.ResolveLauncherExecutingPath(), ManifestFileName);
                if (!File.Exists(manifestPath))
                    return; // feature dormant until we ship the final file list

                List<Entry> entries = ParseManifest(File.ReadAllText(manifestPath));
                if (entries.Count == 0)
                    return;

                List<string> zhInstalls = InstallLocator.FindZeroHourInstalls();
                List<string> generalsInstalls = InstallLocator.FindGeneralsInstalls();
                string baseDir = MainForm.ResolveLauncherExecutingPath();

                foreach (Entry entry in entries)
                {
                    try
                    {
                        string target = Path.Combine(baseDir, entry.RelativePath.Replace('/', '\\'));
                        if (File.Exists(target))
                            continue; // present, nothing to do

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
            }
            catch
            {
                // A broken manifest or IO hiccup must never keep the launcher from starting.
            }

            if (repaired.Count > 0 || failed.Count > 0)
                ShowReport(repaired, failed);
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
                File.Copy(source, target, false);
                return true;
            }

            entry.Error = "注册表给出的 " + gameName + " 目录中也没有该文件 / not found in any registry-located " +
                          gameName + " install";
            return false;
        }

        private static async Task<bool> DownloadFromS3(Entry entry, string target)
        {
            if (string.IsNullOrEmpty(MainForm.S3_BaseUrl))
            {
                entry.Error = "S3 下载通道未配置 / the S3 download channel is not configured yet";
                return false;
            }

            // Backslashes become path segments; segments are escaped so spaces survive the URL.
            string urlPath = entry.RelativePath.Replace('\\', '/');
            string[] segments = urlPath.Split('/');
            for (int i = 0; i < segments.Length; i++)
                segments[i] = Uri.EscapeUriString(segments[i]);
            string url = MainForm.S3_BaseUrl + string.Join("/", segments);

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            await MainForm.DownloadFileSimple(url, target, TimeSpan.FromMinutes(10));
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
