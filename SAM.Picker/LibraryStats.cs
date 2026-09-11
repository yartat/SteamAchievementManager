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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using SAM.API;

namespace SAM.Picker
{
    internal readonly struct GameStats
    {
        public readonly int PlaytimeMinutes;
        public readonly int AchievementsTotal;
        public readonly int AchievementsEarned;
        public readonly DateTime? LastPlayed;

        public GameStats(int playtimeMinutes, int achievementsTotal, int achievementsEarned, DateTime? lastPlayed)
        {
            this.PlaytimeMinutes = playtimeMinutes;
            this.AchievementsTotal = achievementsTotal;
            this.AchievementsEarned = achievementsEarned;
            this.LastPlayed = lastPlayed;
        }
    }

    /// <summary>
    /// Reads playtime and achievement counts out of Steam's own on-disk caches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live <c>ISteamUserStats</c> interface is scoped to the one app id the
    /// process was initialised with — that is precisely why SAM launches a
    /// separate SAM.Game process per game. The picker therefore cannot ask Steam
    /// for another game's achievements, and has to read the caches instead.
    /// </para>
    /// <para>
    /// Totals come from <c>UserGameStatsSchema_&lt;appid&gt;.bin</c>. Earned
    /// counts come from the per-block <c>data</c> bitfield in
    /// <c>UserGameStats_&lt;accountid&gt;_&lt;appid&gt;.bin</c>, masked to the
    /// bits the schema actually defines. Both were validated against the live
    /// API for several games and matched exactly.
    /// </para>
    /// <para>
    /// Coverage is partial by nature: Steam only writes these files for games
    /// that have been launched, so anything else reports <c>null</c> rather than
    /// zero. Do not conflate the two — zero achievements and unknown are
    /// different answers.
    /// </para>
    /// </remarks>
    internal sealed class LibraryStats
    {
        private readonly struct LocalPlay
        {
            public readonly int Minutes;
            public readonly DateTime? LastPlayed;

            public LocalPlay(int minutes, DateTime? lastPlayed)
            {
                this.Minutes = minutes;
                this.LastPlayed = lastPlayed;
            }
        }

        private readonly string _StatsPath;
        private readonly string _AccountId;
        private readonly Dictionary<uint, LocalPlay> _Playtime;

        private LibraryStats(string statsPath, string accountId, Dictionary<uint, LocalPlay> playtime)
        {
            this._StatsPath = statsPath;
            this._AccountId = accountId;
            this._Playtime = playtime;
        }

        public static LibraryStats Create(ulong steamId)
        {
            var installPath = Steam.GetInstallPath();
            if (string.IsNullOrEmpty(installPath) == true)
            {
                return null;
            }

            // userdata and the UserGameStats file names are keyed by the 32-bit
            // account id, not the full 64-bit SteamID.
            var accountId = (steamId & 0xFFFFFFFFUL).ToString(CultureInfo.InvariantCulture);

            return new(
                Path.Combine(installPath, "appcache", "stats"),
                accountId,
                LoadPlaytime(installPath, accountId));
        }

        private static Dictionary<uint, LocalPlay> LoadPlaytime(string installPath, string accountId)
        {
            Dictionary<uint, LocalPlay> result = new();

            var path = Path.Combine(installPath, "userdata", accountId, "config", "localconfig.vdf");
            if (File.Exists(path) == false)
            {
                return result;
            }

            var root = TextKeyValue.LoadFromFile(path);
            var apps = root["UserLocalConfigStore"]["Software"]["Valve"]["Steam"]["apps"];
            if (apps.Valid == false)
            {
                return result;
            }

            foreach (var kv in apps.Children)
            {
                if (uint.TryParse(kv.Key, out var appId) == false)
                {
                    continue;
                }

                var minutes = kv.Value["Playtime"].AsInteger(0);
                var lastPlayed = kv.Value["LastPlayed"].AsInteger(0);

                if (minutes <= 0 && lastPlayed <= 0)
                {
                    continue;
                }

                result[appId] = new(
                    minutes,
                    lastPlayed > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(lastPlayed).LocalDateTime
                        : null);
            }

            return result;
        }

        public GameStats? TryGet(uint appId)
        {
            var schema = this.LoadSchemaBits(appId, out var total);
            this._Playtime.TryGetValue(appId, out var play);

            if (schema == null)
            {
                // No schema cached: playtime alone is still worth reporting.
                return play.Minutes > 0 || play.LastPlayed.HasValue == true
                    ? new GameStats(play.Minutes, -1, -1, play.LastPlayed)
                    : null;
            }

            var earned = this.CountEarned(appId, schema);
            return new(play.Minutes, total, earned, play.LastPlayed);
        }

        /// <summary>stat block id -&gt; bitmask of achievement bits the schema defines.</summary>
        private Dictionary<string, uint> LoadSchemaBits(uint appId, out int total)
        {
            total = 0;

            var path = Path.Combine(this._StatsPath, $"UserGameStatsSchema_{appId}.bin");
            if (File.Exists(path) == false)
            {
                return null;
            }

            var kv = KeyValue.LoadAsBinary(path);
            if (kv == null)
            {
                return null;
            }

            var stats = kv[appId.ToString(CultureInfo.InvariantCulture)]["stats"];
            if (stats.Valid == false || stats.Children == null)
            {
                return null;
            }

            Dictionary<string, uint> result = new();
            foreach (var stat in stats.Children)
            {
                if (stat.Valid == false || stat.Children == null)
                {
                    continue;
                }

                uint mask = 0;
                var count = 0;
                foreach (var bits in stat.Children.Where(
                    b => string.Equals(b.Name, "bits", StringComparison.OrdinalIgnoreCase) == true))
                {
                    if (bits.Valid == false || bits.Children == null)
                    {
                        continue;
                    }
                    foreach (var bit in bits.Children)
                    {
                        if (string.IsNullOrEmpty(bit["name"].AsString("")) == true)
                        {
                            continue;
                        }
                        if (int.TryParse(bit.Name, out var index) == true && index >= 0 && index < 32)
                        {
                            mask |= 1u << index;
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    result[stat.Name] = mask;
                    total += count;
                }
            }

            return result.Count > 0 ? result : null;
        }

        private int CountEarned(uint appId, Dictionary<string, uint> schemaBits)
        {
            var path = Path.Combine(this._StatsPath, $"UserGameStats_{this._AccountId}_{appId}.bin");
            if (File.Exists(path) == false)
            {
                return 0;
            }

            var kv = KeyValue.LoadAsBinary(path);
            var cache = kv?["cache"];
            if (cache == null || cache.Valid == false || cache.Children == null)
            {
                return 0;
            }

            var earned = 0;
            foreach (var block in cache.Children)
            {
                if (schemaBits.TryGetValue(block.Name, out var mask) == false)
                {
                    continue;
                }
                var data = unchecked((uint)block["data"].AsInteger(0));
                earned += BitOperations.PopCount(data & mask);
            }
            return earned;
        }
    }
}
