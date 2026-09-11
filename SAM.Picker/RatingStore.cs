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
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SAM.Picker
{
    /// <summary>
    /// Persists the user's own like/dislike ratings.
    /// </summary>
    /// <remarks>
    /// Stored under <c>%LOCALAPPDATA%\SteamAchievementManager\ratings.json</c>
    /// rather than next to the executable: this is per-user data, and the app
    /// directory is not reliably writable. See <see cref="OwnRating"/> for why
    /// this is local-only and never round-trips to Steam.
    /// </remarks>
    internal sealed class RatingStore
    {
        private readonly string _Path;
        private readonly Dictionary<uint, OwnRating> _Ratings;

        private RatingStore(string path, Dictionary<uint, OwnRating> ratings)
        {
            this._Path = path;
            this._Ratings = ratings;
        }

        public static RatingStore Load()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamAchievementManager",
                "ratings.json");

            Dictionary<uint, OwnRating> ratings = new();
            try
            {
                if (File.Exists(path) == true)
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path));
                    if (parsed != null)
                    {
                        foreach (var kv in parsed)
                        {
                            if (uint.TryParse(kv.Key, out var id) == true &&
                                Enum.IsDefined(typeof(OwnRating), kv.Value) == true)
                            {
                                ratings[id] = (OwnRating)kv.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A corrupt or unreadable ratings file is not worth blocking
                // startup over; it is a convenience, not user content.
            }

            return new(path, ratings);
        }

        public OwnRating Get(uint appId)
        {
            return this._Ratings.TryGetValue(appId, out var rating) == true
                ? rating
                : OwnRating.None;
        }

        /// <summary>Returns false if the change could not be persisted.</summary>
        public bool Set(uint appId, OwnRating rating)
        {
            if (rating == OwnRating.None)
            {
                this._Ratings.Remove(appId);
            }
            else
            {
                this._Ratings[appId] = rating;
            }
            return this.Save();
        }

        private bool Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this._Path));

                Dictionary<string, int> payload = new();
                foreach (var kv in this._Ratings)
                {
                    payload[kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = (int)kv.Value;
                }

                File.WriteAllText(
                    this._Path,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions() { WriteIndented = true }));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
