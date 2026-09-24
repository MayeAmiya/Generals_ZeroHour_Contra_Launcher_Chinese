using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Contra
{
    /// <summary>
    ///     Reads, repairs, and writes the Zero Hour <c>Options.ini</c> while keeping the values a user already set.
    /// </summary>
    /// <remarks>
    ///     The game marks <c>Options.ini</c> read-only while it runs and rewrites it on exit, so every access clears the
    ///     read-only attribute before touching the file. Entries the launcher does not know about are preserved, and their
    ///     original order is kept so the file stays close to what the game wrote.
    /// </remarks>
    internal sealed class OptionsIniFile
    {
        /// <summary>
        ///     Keys the launcher and the game both expect. A file missing any of them is considered incomplete.
        /// </summary>
        private static readonly string[] RequiredKeys =
        {
            "IdealStaticGameLOD",
            "Resolution",
            "BuildingOcclusion",
            "DynamicLOD",
            "ExtraAnimations",
            "HeatEffects",
            "ShowSoftWaterEdge",
            "ShowTrees",
            "StaticGameLOD",
            "MaxParticleCount",
            "TextureReduction",
            "UseCloudMap",
            "UseLightMap",
            "UseShadowDecals",
            "UseShadowVolumes"
        };

        /// <summary>
        ///     Fallback values keyed by option name. <c>Resolution</c> is absent because it depends on the current display.
        /// </summary>
        private static readonly Dictionary<string, string> FallbackValues =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "IdealStaticGameLOD", "High" },
                { "BuildingOcclusion", "Yes" },
                { "DynamicLOD", "Yes" },
                { "ExtraAnimations", "Yes" },
                { "HeatEffects", "No" },
                { "ShowSoftWaterEdge", "Yes" },
                { "ShowTrees", "Yes" },
                { "StaticGameLOD", "Custom" },
                { "MaxParticleCount", "2500" },
                { "TextureReduction", "0" },
                { "UseCloudMap", "Yes" },
                { "UseLightMap", "Yes" },
                { "UseShadowDecals", "Yes" },
                { "UseShadowVolumes", "Yes" }
            };

        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private OptionsIniFile()
        {
        }

        /// <summary>
        ///     Reads an options file.
        /// </summary>
        /// <returns>
        ///     A document holding every entry that could be read. A missing path yields an empty document, while an existing
        ///     file that cannot be read yields <see langword="null" /> so a caller never overwrites what it failed to read.
        /// </returns>
        public static OptionsIniFile Load(string path)
        {
            OptionsIniFile document = new OptionsIniFile();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return document;
            }

            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith(";", StringComparison.Ordinal) ||
                        trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int separator = trimmed.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    string key = trimmed.Substring(0, separator).Trim();
                    string value = trimmed.Substring(separator + 1).Trim();
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    document.Set(key, value);
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            return document;
        }

        /// <summary>
        ///     Gets a value, or an empty string when the key is absent.
        /// </summary>
        public string Get(string key)
        {
            string value;
            return _values.TryGetValue(key, out value) ? value : string.Empty;
        }

        /// <summary>
        ///     Sets a value, keeping the position of an existing entry and appending a new one otherwise.
        /// </summary>
        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            if (!_values.ContainsKey(key))
            {
                _order.Add(key);
            }

            _values[key] = value ?? string.Empty;
        }

        /// <summary>
        ///     Reports the required keys that are missing or blank.
        /// </summary>
        public List<string> GetMissingRequiredKeys()
        {
            List<string> missing = new List<string>();
            foreach (string key in RequiredKeys)
            {
                if (Get(key).Length == 0)
                {
                    missing.Add(key);
                }
            }

            return missing;
        }

        /// <summary>
        ///     Fills every missing required key without overwriting anything the user already set.
        /// </summary>
        /// <param name="resolution">The <c>Resolution</c> value to use when the file has none, for example <c>1920 1080</c>.</param>
        /// <returns>The number of keys that were added.</returns>
        public int FillDefaults(string resolution)
        {
            int added = 0;
            foreach (string key in GetMissingRequiredKeys())
            {
                string fallback;
                if (string.Equals(key, "Resolution", StringComparison.OrdinalIgnoreCase))
                {
                    fallback = string.IsNullOrEmpty(resolution) ? "1024 768" : resolution;
                }
                else if (!FallbackValues.TryGetValue(key, out fallback))
                {
                    continue;
                }

                Set(key, fallback);
                added++;
            }

            return added;
        }

        /// <summary>
        ///     Writes the document back, preserving entry order and clearing the read-only attribute first.
        /// </summary>
        public bool Save(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            StringBuilder builder = new StringBuilder();
            foreach (string key in _order)
            {
                builder.Append(key).Append(" = ").Append(Get(key)).Append(Environment.NewLine);
            }

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
