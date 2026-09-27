using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Contra
{
    /// <summary>
    ///     Locates local Generals / Zero Hour installations through the Windows registry so the
    ///     launcher can repair missing base-game files instead of failing on a partial install.
    ///     Uses the same probe order as the GeneralsGameCode installation contract:
    ///     the retail (EA App / CD) release registers under
    ///     "Electronic Arts\EA Games\Command and Conquer Generals Zero Hour" / "Generals",
    ///     the Steam release writes "EA Games\ZeroHour" (and a lowercase "installPath"),
    ///     and The First Decade bundle keeps both games under its gr_folder / zh_folder values.
    ///     Both registry views (32 and 64 bit) are probed, because 32 bit installers write
    ///     under Wow6432Node while the Steam release writes the native view.
    /// </summary>
    internal static class InstallLocator
    {
        private class RegistryProbe
        {
            public readonly string KeyName;
            public readonly string ValueName;

            public RegistryProbe(string keyName, string valueName)
            {
                KeyName = keyName;
                ValueName = valueName;
            }
        }

        private static readonly RegistryProbe[] ZeroHourProbes =
        {
            new RegistryProbe(@"SOFTWARE\Electronic Arts\EA Games\Command and Conquer Generals Zero Hour", "InstallPath"),
            new RegistryProbe(@"SOFTWARE\Electronic Arts\EA Games\ZeroHour", "installPath"),
            new RegistryProbe(@"SOFTWARE\Electronic Arts\EA Games\Command and Conquer The First Decade", "zh_folder"),
        };

        private static readonly RegistryProbe[] GeneralsProbes =
        {
            new RegistryProbe(@"SOFTWARE\Electronic Arts\EA Games\Generals", "InstallPath"),
            new RegistryProbe(@"SOFTWARE\Electronic Arts\EA Games\Generals", "installPath"),
            new RegistryProbe(@"SOFTWARE\Electronic Arts\EA Games\Command and Conquer The First Decade", "gr_folder"),
        };

        /// <summary>
        ///     Returns every distinct, existing directory the registry offers for Zero Hour,
        ///     best candidate first. Empty when nothing usable is registered.
        /// </summary>
        public static List<string> FindZeroHourInstalls()
        {
            return FindInstallCandidates(ZeroHourProbes);
        }

        /// <summary>
        ///     Returns every distinct, existing directory the registry offers for the base
        ///     Generals game, best candidate first. Empty when nothing usable is registered.
        /// </summary>
        public static List<string> FindGeneralsInstalls()
        {
            return FindInstallCandidates(GeneralsProbes);
        }

        private static List<string> FindInstallCandidates(RegistryProbe[] probes)
        {
            var candidates = new List<string>();

            foreach (RegistryProbe probe in probes)
            {
                foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                {
                    string path = ReadValue(view, probe.KeyName, probe.ValueName);
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    path = Normalize(path);
                    if (path.Length == 0 || !Directory.Exists(path))
                        continue;

                    if (candidates.Exists(c => string.Equals(c, path, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    candidates.Add(path);
                }
            }

            return candidates;
        }

        private static string ReadValue(RegistryView view, string keyName, string valueName)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey key = baseKey.OpenSubKey(keyName, false))
                {
                    if (key == null)
                        return null;

                    // DoNotExpandEnvironmentNames: some installers store REG_EXPAND_SZ values;
                    // we expand explicitly in Normalize so unparseable variables never throw here.
                    return key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                }
            }
            catch
            {
                // Registry candidates are optional; anything unreadable just yields no candidate.
                return null;
            }
        }

        private static string Normalize(string raw)
        {
            try
            {
                string candidate = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                return candidate.TrimEnd('\\');
            }
            catch
            {
                return "";
            }
        }
    }
}
