// Copyright 2026 CyberCraft Lab, OTH Regensburg, Prof. Christophe Barlieb
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.IO;
using Newtonsoft.Json;
using CCL_Clay3DP.Models;

namespace CCL_Clay3DP.Settings
{
    public static class SettingsManager
    {
        // Current settings location — CCL_Clay3DP after the 2026-04 rename
        private static readonly string AppDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "McNeel", "Rhinoceros", "8.0", "Plug-ins");

        private static readonly string ConfigDir = Path.Combine(AppDataRoot, "CCL_Clay3DP");
        private static readonly string ConfigFile = Path.Combine(ConfigDir, "settings.json");

        // Legacy settings location from before the rename — migrated on first load
        private static readonly string LegacyConfigDir = Path.Combine(AppDataRoot, "Auto3DPipeline");
        private static readonly string LegacyConfigFile = Path.Combine(LegacyConfigDir, "settings.json");

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static PipelineSettings Load()
        {
            // Migrate legacy settings on first load after rename
            MigrateLegacySettingsIfNeeded();

            if (!File.Exists(ConfigFile))
                return new PipelineSettings();

            try
            {
                string json = File.ReadAllText(ConfigFile);
                return JsonConvert.DeserializeObject<PipelineSettings>(json, JsonSettings)
                       ?? new PipelineSettings();
            }
            catch
            {
                return new PipelineSettings();
            }
        }

        public static void Save(PipelineSettings settings)
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);

            string json = JsonConvert.SerializeObject(settings, JsonSettings);
            File.WriteAllText(ConfigFile, json);
        }

        /// <summary>
        /// If the user has settings from the old Auto3DPipeline plugin and
        /// no settings yet at the new CCL_Clay3DP location, copy them over
        /// so their configuration is preserved.
        /// </summary>
        private static void MigrateLegacySettingsIfNeeded()
        {
            if (File.Exists(ConfigFile))
                return; // already migrated or has fresh settings
            if (!File.Exists(LegacyConfigFile))
                return; // no legacy settings to migrate

            try
            {
                if (!Directory.Exists(ConfigDir))
                    Directory.CreateDirectory(ConfigDir);
                File.Copy(LegacyConfigFile, ConfigFile, overwrite: false);
            }
            catch
            {
                // If migration fails for any reason, we fall back to defaults —
                // no data loss since the legacy file is still on disk.
            }
        }
    }
}
