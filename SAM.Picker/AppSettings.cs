/* Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAM.Picker
{
    /// <summary>
    /// User-configurable cache locations.
    /// </summary>
    /// <remarks>
    /// The settings file itself lives at a fixed <c>~/.sam/settings.json</c>.
    /// It cannot live inside the configurable database directory — that is
    /// where the path to read would have to come from.
    /// </remarks>
    internal sealed class AppSettings
    {
        public string DatabasePath { get; set; }
        public string IconCachePath { get; set; }

        [JsonIgnore]
        public static string HomeDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".sam");

        [JsonIgnore]
        public static string SettingsPath => Path.Combine(HomeDirectory, "settings.json");

        public static string DefaultDatabasePath => Path.Combine(HomeDirectory, "games.db");

        public static string DefaultIconCachePath => Path.Combine(HomeDirectory, "icons");

        public static AppSettings Load()
        {
            AppSettings settings = null;
            try
            {
                if (File.Exists(SettingsPath) == true)
                {
                    settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                }
            }
            catch (Exception)
            {
                // A corrupt settings file falls back to defaults rather than
                // blocking startup.
            }

            settings ??= new();
            if (string.IsNullOrWhiteSpace(settings.DatabasePath) == true)
            {
                settings.DatabasePath = DefaultDatabasePath;
            }
            if (string.IsNullOrWhiteSpace(settings.IconCachePath) == true)
            {
                settings.IconCachePath = DefaultIconCachePath;
            }
            return settings;
        }

        /// <summary>Returns false if the settings could not be written.</summary>
        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(HomeDirectory);
                File.WriteAllText(
                    SettingsPath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions() { WriteIndented = true }));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public AppSettings Clone()
        {
            return new() { DatabasePath = this.DatabasePath, IconCachePath = this.IconCachePath };
        }
    }
}
