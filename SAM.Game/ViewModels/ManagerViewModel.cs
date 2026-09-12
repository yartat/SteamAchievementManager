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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SAM.API;
using SAM.Shared;
using static SAM.Game.InvariantShorthand;
using APITypes = SAM.API.Types;

namespace SAM.Game.ViewModels
{
    internal sealed partial class ManagerViewModel : ProgressViewModel
    {
        private static readonly HttpClient _Http = new();

        private readonly long _GameId;
        private readonly long _AccountId;
        private readonly string _Language;
        private readonly API.Client _SteamClient;
        private readonly API.Callbacks.UserStatsReceived _UserStatsReceivedCallback;
        private readonly DispatcherTimer _CallbackTimer;

        private readonly List<Stats.AchievementDefinition> _AchievementDefinitions = new();
        private readonly List<Stats.StatDefinition> _StatDefinitions = new();

        private readonly ConcurrentQueue<Stats.AchievementInfo> _IconQueue = new();

        // Queued and finished since the queue last drained, so the bar has a
        // denominator. UI thread only, like the queue itself.
        private int _IconsQueued;
        private int _IconsCompleted;
        private readonly SemaphoreSlim _IconSignal = new(0);
        private readonly Dictionary<string, Bitmap> _IconBitmaps = new();

        /// <summary>The same icon directory the picker keeps its capsules in.</summary>
        private readonly IconCache _IconCache;

        /// <summary>Null when the database could not be opened; every start is then a cold one.</summary>
        private AchievementCache _Cache;

        private Task _SaveTask = Task.CompletedTask;

        private bool _IsUpdatingAchievementList;

        public ObservableCollection<Stats.AchievementInfo> Achievements { get; } = new();
        public ObservableCollection<Stats.StatInfo> Statistics { get; } = new();

        [ObservableProperty]
        private string _Title = "Steam Achievement Manager 8.0";

        [ObservableProperty]
        private string _StatusText = "";

        [ObservableProperty]
        private bool _IsInputEnabled = true;

        [ObservableProperty]
        private bool _IsStatsEditingEnabled;

        private string _MatchingString = "";
        public string MatchingString
        {
            get => this._MatchingString;
            set
            {
                if (this.SetProperty(ref this._MatchingString, value) == true)
                {
                    this.GetAchievements();
                }
            }
        }

        private bool _ShowLockedOnly;
        public bool ShowLockedOnly
        {
            get => this._ShowLockedOnly;
            set
            {
                if (this.SetProperty(ref this._ShowLockedOnly, value) == false)
                {
                    return;
                }
                if (value == true && this._ShowUnlockedOnly == true)
                {
                    this._ShowUnlockedOnly = false;
                    this.OnPropertyChanged(nameof(this.ShowUnlockedOnly));
                }
                this.GetAchievements();
            }
        }

        private bool _ShowUnlockedOnly;
        public bool ShowUnlockedOnly
        {
            get => this._ShowUnlockedOnly;
            set
            {
                if (this.SetProperty(ref this._ShowUnlockedOnly, value) == false)
                {
                    return;
                }
                if (value == true && this._ShowLockedOnly == true)
                {
                    this._ShowLockedOnly = false;
                    this.OnPropertyChanged(nameof(this.ShowLockedOnly));
                }
                this.GetAchievements();
            }
        }

        /// <summary>Message, and whether it is informational rather than an error.</summary>
        public event Action<string, bool> MessageRaised;

        /// <summary>Title, message; the handler returns the user's answer.</summary>
        public event Func<string, string, Task<bool>> ConfirmRequested;

        public ManagerViewModel(long gameId, API.Client client)
        {
            this._GameId = gameId;
            this._SteamClient = client;

            // The cache files are keyed by the 32-bit account id, the same way
            // Steam's own on-disk stats caches are.
            this._AccountId = (long)(client.SteamUser.GetSteamId() & 0xFFFFFFFFUL);
            this._Language = client.SteamApps008.GetCurrentGameLanguage();

            var settings = AppSettings.Load();
            this._IconCache = new(settings.IconCachePath);
            this.OpenCache(settings.DatabasePath);

            var name = client.SteamApps001.GetAppData((uint)gameId, "name");
            this.Title = "Steam Achievement Manager 8.0 | " +
                (name ?? gameId.ToString(CultureInfo.InvariantCulture));

            this._UserStatsReceivedCallback = client.CreateAndRegisterCallback<API.Callbacks.UserStatsReceived>();
            this._UserStatsReceivedCallback.OnRun += this.OnUserStatsReceived;

            this._CallbackTimer = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, this.OnTimer);
            this._CallbackTimer.Start();

            Task iconWorker = this.RunIconWorkerAsync();
            GC.KeepAlive(iconWorker);

            this.RefreshStats();

            // RefreshStats only asks; the answer arrives on the callback timer.
            // Until it does, show what was cached last time rather than an empty
            // window. Input stays disabled, so nothing can be committed from it.
            this.LoadFromCache();
        }

        private void OpenCache(string databasePath)
        {
            try
            {
                this._Cache = AchievementCache.Open(databasePath);
            }
            catch (Exception)
            {
                // A cache that will not open must not stop the app.
                this._Cache = null;
            }
        }

        public void Shutdown()
        {
            this._CallbackTimer.Stop();
            this._Cache?.Dispose();
            this._Cache = null;
        }

        private void OnTimer(object sender, EventArgs e)
        {
            this._SteamClient.RunCallbacks(false);
        }

        private static string TranslateError(int id) => id switch
        {
            2 => "generic error -- this usually means you don't own the game",
            _ => _($"{id}"),
        };

        #region Loading

        private void OnUserStatsReceived(APITypes.UserStatsReceived param)
        {
            if (param.Result != 1)
            {
                this.StatusText = $"Error while retrieving stats: {TranslateError(param.Result)}";
                this.EndProgress();
                this.IsInputEnabled = true;
                return;
            }

            if (this.LoadUserGameStatsSchema() == false)
            {
                this.StatusText = "Failed to load schema.";
                this.EndProgress();
                this.IsInputEnabled = true;
                return;
            }

            try
            {
                this.GetAchievements();
            }
            catch (Exception e)
            {
                this.StatusText = "Error when handling achievements retrieval.";
                this.EndProgress();
                this.IsInputEnabled = true;
                this.MessageRaised?.Invoke("Error when handling achievements retrieval:\n" + e, false);
                return;
            }

            try
            {
                this.GetStatistics();
            }
            catch (Exception e)
            {
                this.StatusText = "Error when handling stats retrieval.";
                this.EndProgress();
                this.IsInputEnabled = true;
                this.MessageRaised?.Invoke("Error when handling stats retrieval:\n" + e, false);
                return;
            }

            this.SaveCache();

            this.EndProgress();
            this.StatusText =
                $"Retrieved {this.Achievements.Count} achievements and {this.Statistics.Count} statistics.";
            this.IsInputEnabled = true;
        }

        /// <summary>
        /// Paints the achievement list from the database before Steam has
        /// answered. Everything here is already-persisted data.
        /// </summary>
        private void LoadFromCache()
        {
            if (this._Cache == null)
            {
                return;
            }

            List<CachedAchievement> rows;
            try
            {
                rows = this._Cache.Load(this._AccountId, this._GameId, this._Language);
            }
            catch (Exception)
            {
                return;
            }

            if (rows.Count == 0)
            {
                return;
            }

            foreach (var row in rows)
            {
                Stats.AchievementInfo info = new()
                {
                    Id = row.Id,
                    IsAchieved = row.IsAchieved,
                    OriginalValue = row.IsAchieved,
                    UnlockTime = row.UnlockTime,
                    // Same fallback GetAchievements applies: a game with no
                    // separate greyed-out icon reuses the normal one.
                    IconNormal = string.IsNullOrEmpty(row.IconNormal) ? null : row.IconNormal,
                    IconLocked = string.IsNullOrEmpty(row.IconLocked) ? row.IconNormal : row.IconLocked,
                    Permission = row.Permission,
                    Name = row.Name,
                    Description = row.Description,
                };

                info.PropertyChanged += this.OnAchievementPropertyChanged;
                this.Achievements.Add(info);
                this.EnqueueIcon(info);
            }

            this.StatusText = $"Showing {rows.Count} cached achievements. Refreshing...";
        }

        /// <summary>
        /// Writes the full achievement list back, off the UI thread. Built from
        /// the definitions rather than from <see cref="Achievements"/>, which is
        /// whatever the filters last left on screen.
        /// </summary>
        private void SaveCache()
        {
            if (this._Cache == null || this._AchievementDefinitions.Count == 0)
            {
                return;
            }

            List<CachedAchievement> rows = new(this._AchievementDefinitions.Count);
            foreach (var def in this._AchievementDefinitions)
            {
                if (string.IsNullOrEmpty(def.Id) == true)
                {
                    continue;
                }

                if (this._SteamClient.SteamUserStats.GetAchievementAndUnlockTime(
                    def.Id,
                    out bool isAchieved,
                    out var unlockTime) == false)
                {
                    continue;
                }

                rows.Add(new()
                {
                    Id = def.Id,
                    Name = def.Name,
                    Description = def.Description,
                    IconNormal = def.IconNormal,
                    IconLocked = def.IconLocked,
                    Permission = def.Permission,
                    IsAchieved = isAchieved,
                    UnlockTime = isAchieved == true && unlockTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(unlockTime).LocalDateTime
                        : null,
                });
            }

            if (rows.Count == 0)
            {
                return;
            }

            var cache = this._Cache;
            var accountId = this._AccountId;
            var gameId = this._GameId;
            var language = this._Language;

            // Chained rather than fired off: a SqliteConnection cannot serve two
            // commands at once, and hitting Refresh twice in a row would
            // otherwise overlap two writes on this one.
            this._SaveTask = this._SaveTask.ContinueWith(
                previous =>
                {
                    try
                    {
                        cache.Sync(accountId, gameId, language, rows);
                    }
                    catch (Exception)
                    {
                        // A cache that cannot be written is a slow app, not a broken one.
                    }
                },
                TaskScheduler.Default);
        }

        private void RefreshStats()
        {
            this.Achievements.Clear();
            this.Statistics.Clear();

            var steamId = this._SteamClient.SteamUser.GetSteamId();

            // This still triggers the UserStatsReceived callback, in addition to the callresult.
            // No need to implement callresults for the time being.
            var callHandle = this._SteamClient.SteamUserStats.RequestUserStats(steamId);
            if (callHandle == API.CallHandle.Invalid)
            {
                this.MessageRaised?.Invoke("Failed.", false);
                return;
            }

            this.StatusText = "Retrieving stat information...";
            this.BeginProgress("Waiting for Steam...");
            this.IsInputEnabled = false;
        }

        private static string GetLocalizedString(KeyValue kv, string language, string defaultValue)
        {
            var name = kv[language].AsString("");
            if (string.IsNullOrEmpty(name) == false)
            {
                return name;
            }

            if (language != "english")
            {
                name = kv["english"].AsString("");
                if (string.IsNullOrEmpty(name) == false)
                {
                    return name;
                }
            }

            name = kv.AsString("");
            if (string.IsNullOrEmpty(name) == false)
            {
                return name;
            }

            return defaultValue;
        }

        private bool LoadUserGameStatsSchema()
        {
            string path;
            try
            {
                var fileName = _($"UserGameStatsSchema_{this._GameId}.bin");
                path = API.Steam.GetInstallPath();
                path = Path.Combine(path, "appcache", "stats", fileName);
                if (File.Exists(path) == false)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

            var kv = KeyValue.LoadAsBinary(path);
            if (kv == null)
            {
                return false;
            }

            // The same value the cache is keyed on, so cached text and freshly
            // parsed text are always in the same language.
            var currentLanguage = this._Language;

            this._AchievementDefinitions.Clear();
            this._StatDefinitions.Clear();

            var stats = kv[this._GameId.ToString(CultureInfo.InvariantCulture)]["stats"];
            if (stats.Valid == false || stats.Children == null)
            {
                return false;
            }

            foreach (var stat in stats.Children)
            {
                if (stat.Valid == false)
                {
                    continue;
                }

                APITypes.UserStatType type;

                // schema in the new format?
                var typeNode = stat["type"];
                if (typeNode.Valid == true && typeNode.Type == KeyValueType.String)
                {
                    if (Enum.TryParse((string)typeNode.Value, true, out type) == false)
                    {
                        type = APITypes.UserStatType.Invalid;
                    }
                }
                else
                {
                    type = APITypes.UserStatType.Invalid;
                }

                // schema in the old format?
                if (type == APITypes.UserStatType.Invalid)
                {
                    var typeIntNode = stat["type_int"];
                    var rawType = typeIntNode.Valid == true
                        ? typeIntNode.AsInteger(0)
                        : typeNode.AsInteger(0);
                    type = (APITypes.UserStatType)rawType;
                }

                switch (type)
                {
                    case APITypes.UserStatType.Invalid:
                    {
                        break;
                    }

                    case APITypes.UserStatType.Integer:
                    {
                        var id = stat["name"].AsString("");
                        var name = GetLocalizedString(stat["display"]["name"], currentLanguage, id);

                        this._StatDefinitions.Add(new Stats.IntegerStatDefinition()
                        {
                            Id = stat["name"].AsString(""),
                            DisplayName = name,
                            MinValue = stat["min"].AsInteger(int.MinValue),
                            MaxValue = stat["max"].AsInteger(int.MaxValue),
                            MaxChange = stat["maxchange"].AsInteger(0),
                            IncrementOnly = stat["incrementonly"].AsBoolean(false),
                            SetByTrustedGameServer = stat["bSetByTrustedGS"].AsBoolean(false),
                            DefaultValue = stat["default"].AsInteger(0),
                            Permission = stat["permission"].AsInteger(0),
                        });
                        break;
                    }

                    case APITypes.UserStatType.Float:
                    case APITypes.UserStatType.AverageRate:
                    {
                        var id = stat["name"].AsString("");
                        var name = GetLocalizedString(stat["display"]["name"], currentLanguage, id);

                        this._StatDefinitions.Add(new Stats.FloatStatDefinition()
                        {
                            Id = stat["name"].AsString(""),
                            DisplayName = name,
                            MinValue = stat["min"].AsFloat(float.MinValue),
                            MaxValue = stat["max"].AsFloat(float.MaxValue),
                            MaxChange = stat["maxchange"].AsFloat(0.0f),
                            IncrementOnly = stat["incrementonly"].AsBoolean(false),
                            DefaultValue = stat["default"].AsFloat(0.0f),
                            Permission = stat["permission"].AsInteger(0),
                        });
                        break;
                    }

                    case APITypes.UserStatType.Achievements:
                    case APITypes.UserStatType.GroupAchievements:
                    {
                        if (stat.Children != null)
                        {
                            foreach (var bits in stat.Children.Where(
                                b => string.Compare(b.Name, "bits", StringComparison.InvariantCultureIgnoreCase) == 0))
                            {
                                if (bits.Valid == false || bits.Children == null)
                                {
                                    continue;
                                }

                                foreach (var bit in bits.Children)
                                {
                                    var id = bit["name"].AsString("");
                                    var name = GetLocalizedString(bit["display"]["name"], currentLanguage, id);
                                    var desc = GetLocalizedString(bit["display"]["desc"], currentLanguage, "");

                                    this._AchievementDefinitions.Add(new()
                                    {
                                        Id = id,
                                        Name = name,
                                        Description = desc,
                                        IconNormal = bit["display"]["icon"].AsString(""),
                                        IconLocked = bit["display"]["icon_gray"].AsString(""),
                                        IsHidden = bit["display"]["hidden"].AsBoolean(false),
                                        Permission = bit["permission"].AsInteger(0),
                                    });
                                }
                            }
                        }

                        break;
                    }

                    default:
                    {
                        throw new InvalidOperationException("invalid stat type");
                    }
                }
            }

            return true;
        }

        private void GetAchievements()
        {
            if (this._AchievementDefinitions.Count == 0)
            {
                return;
            }

            var textSearch = this.MatchingString.Length > 0 ? this.MatchingString : null;

            this._IsUpdatingAchievementList = true;

            foreach (var existing in this.Achievements)
            {
                existing.PropertyChanged -= this.OnAchievementPropertyChanged;
            }
            this.Achievements.Clear();

            var wantLocked = this.ShowLockedOnly;
            var wantUnlocked = this.ShowUnlockedOnly;

            foreach (var def in this._AchievementDefinitions)
            {
                if (string.IsNullOrEmpty(def.Id) == true)
                {
                    continue;
                }

                if (this._SteamClient.SteamUserStats.GetAchievementAndUnlockTime(
                    def.Id,
                    out bool isAchieved,
                    out var unlockTime) == false)
                {
                    continue;
                }

                bool wanted = (wantLocked == false && wantUnlocked == false) || isAchieved switch
                {
                    true => wantUnlocked,
                    false => wantLocked,
                };
                if (wanted == false)
                {
                    continue;
                }

                if (textSearch != null)
                {
                    if (def.Name.IndexOf(textSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                        def.Description.IndexOf(textSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                }

                Stats.AchievementInfo info = new()
                {
                    Id = def.Id,
                    IsAchieved = isAchieved,
                    OriginalValue = isAchieved,
                    UnlockTime = isAchieved == true && unlockTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(unlockTime).LocalDateTime
                        : null,
                    IconNormal = string.IsNullOrEmpty(def.IconNormal) ? null : def.IconNormal,
                    IconLocked = string.IsNullOrEmpty(def.IconLocked) ? def.IconNormal : def.IconLocked,
                    Permission = def.Permission,
                    Name = def.Name,
                    Description = def.Description,
                };

                info.PropertyChanged += this.OnAchievementPropertyChanged;
                this.Achievements.Add(info);
                this.EnqueueIcon(info);
            }

            this._IsUpdatingAchievementList = false;
        }

        private void OnAchievementPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (this._IsUpdatingAchievementList == true)
            {
                return;
            }
            if (e.PropertyName != nameof(Stats.AchievementInfo.IsAchieved))
            {
                return;
            }
            if (sender is not Stats.AchievementInfo info)
            {
                return;
            }

            if (info.IsProtected == true)
            {
                this._IsUpdatingAchievementList = true;
                info.IsAchieved = info.OriginalValue;
                this._IsUpdatingAchievementList = false;
                this.MessageRaised?.Invoke(
                    "Sorry, but this is a protected achievement and cannot be managed with Steam Achievement Manager.",
                    false);
                return;
            }

            this.EnqueueIcon(info);
        }

        private void GetStatistics()
        {
            this.Statistics.Clear();
            foreach (var stat in this._StatDefinitions)
            {
                if (string.IsNullOrEmpty(stat.Id) == true)
                {
                    continue;
                }

                if (stat is Stats.IntegerStatDefinition intStat)
                {
                    if (this._SteamClient.SteamUserStats.GetStatValue(intStat.Id, out int value) == false)
                    {
                        continue;
                    }
                    this.Statistics.Add(new Stats.IntStatInfo()
                    {
                        Id = intStat.Id,
                        DisplayName = intStat.DisplayName,
                        IntValue = value,
                        OriginalValue = value,
                        IsIncrementOnly = intStat.IncrementOnly,
                        Permission = intStat.Permission,
                    });
                }
                else if (stat is Stats.FloatStatDefinition floatStat)
                {
                    if (this._SteamClient.SteamUserStats.GetStatValue(floatStat.Id, out float value) == false)
                    {
                        continue;
                    }
                    this.Statistics.Add(new Stats.FloatStatInfo()
                    {
                        Id = floatStat.Id,
                        DisplayName = floatStat.DisplayName,
                        FloatValue = value,
                        OriginalValue = value,
                        IsIncrementOnly = floatStat.IncrementOnly,
                        Permission = floatStat.Permission,
                    });
                }
            }
        }

        #endregion

        #region Commands

        [RelayCommand]
        private void Reload()
        {
            this.RefreshStats();
        }

        [RelayCommand]
        private void LockAll()
        {
            this.SetAllAchievements(false);
        }

        [RelayCommand]
        private void UnlockAll()
        {
            this.SetAllAchievements(true);
        }

        [RelayCommand]
        private void InvertAll()
        {
            foreach (var info in this.Achievements)
            {
                if (info.IsProtected == true)
                {
                    continue;
                }
                this._IsUpdatingAchievementList = true;
                info.IsAchieved = info.IsAchieved == false;
                this._IsUpdatingAchievementList = false;
            }
        }

        private void SetAllAchievements(bool value)
        {
            foreach (var info in this.Achievements)
            {
                if (info.IsProtected == true)
                {
                    continue;
                }
                this._IsUpdatingAchievementList = true;
                info.IsAchieved = value;
                this._IsUpdatingAchievementList = false;
            }
        }

        [RelayCommand]
        private void Store()
        {
            var achievements = this.StoreAchievements();
            if (achievements < 0)
            {
                this.RefreshStats();
                return;
            }

            var stats = this.StoreStatistics();
            if (stats < 0)
            {
                this.RefreshStats();
                return;
            }

            if (this._SteamClient.SteamUserStats.StoreStats() == false)
            {
                this.MessageRaised?.Invoke("An error occurred while storing, aborting.", false);
                this.RefreshStats();
                return;
            }

            this.MessageRaised?.Invoke(
                $"Stored {achievements} achievements and {stats} statistics.",
                true);
            this.RefreshStats();
        }

        private int StoreAchievements()
        {
            if (this.Achievements.Count == 0)
            {
                return 0;
            }

            var changed = this.Achievements
                .Where(a => a.IsAchieved != a.OriginalValue)
                .ToList();
            if (changed.Count == 0)
            {
                return 0;
            }

            foreach (var info in changed)
            {
                if (this._SteamClient.SteamUserStats.SetAchievement(info.Id, info.IsAchieved) == false)
                {
                    this.MessageRaised?.Invoke(
                        $"An error occurred while setting the state for {info.Id}, aborting store.",
                        false);
                    return -1;
                }
            }

            return changed.Count;
        }

        private int StoreStatistics()
        {
            if (this.Statistics.Count == 0)
            {
                return 0;
            }

            var statistics = this.Statistics.Where(stat => stat.IsModified == true).ToList();
            if (statistics.Count == 0)
            {
                return 0;
            }

            foreach (var stat in statistics)
            {
                if (stat is Stats.IntStatInfo intStat)
                {
                    if (this._SteamClient.SteamUserStats.SetStatValue(intStat.Id, intStat.IntValue) == false)
                    {
                        this.MessageRaised?.Invoke(
                            $"An error occurred while setting the value for {stat.Id}, aborting store.",
                            false);
                        return -1;
                    }
                }
                else if (stat is Stats.FloatStatInfo floatStat)
                {
                    if (this._SteamClient.SteamUserStats.SetStatValue(floatStat.Id, floatStat.FloatValue) == false)
                    {
                        this.MessageRaised?.Invoke(
                            $"An error occurred while setting the value for {stat.Id}, aborting store.",
                            false);
                        return -1;
                    }
                }
                else
                {
                    throw new InvalidOperationException("unsupported stat type");
                }
            }

            return statistics.Count;
        }

        [RelayCommand]
        private async Task ResetAllStatsAsync()
        {
            if (this.ConfirmRequested == null)
            {
                return;
            }

            if (await this.ConfirmRequested("Warning", "Are you absolutely sure you want to reset stats?") == false)
            {
                return;
            }

            var achievementsToo = await this.ConfirmRequested(
                "Question",
                "Do you want to reset achievements too?");

            if (await this.ConfirmRequested("Warning", "Really really sure?") == false)
            {
                return;
            }

            if (this._SteamClient.SteamUserStats.ResetAllStats(achievementsToo) == false)
            {
                this.MessageRaised?.Invoke("Failed.", false);
                return;
            }

            this.RefreshStats();
        }

        #endregion

        #region Icons

        private void EnqueueIcon(Stats.AchievementInfo info)
        {
            var iconName = info.CurrentIconName;
            if (string.IsNullOrEmpty(iconName) == true)
            {
                return;
            }

            if (this._IconBitmaps.TryGetValue(iconName, out var cached) == true)
            {
                info.Icon = cached;
                return;
            }

            if (this._IconsQueued == 0)
            {
                this.BeginProgress("Loading achievement icons...");
            }

            this._IconQueue.Enqueue(info);
            this._IconsQueued++;
            this._IconSignal.Release();
        }

        /// <summary>
        /// False for a truncated cache file or a reply that is not an image, in
        /// which case the caller falls through to a download.
        /// </summary>
        private bool TryDecodeIcon(string iconName, byte[] data, Stats.AchievementInfo info)
        {
            try
            {
                using MemoryStream stream = new(data, false);
                // Avalonia's Bitmap copies the decoded pixels, so unlike
                // System.Drawing it does not need the stream kept alive.
                Bitmap bitmap = new(stream);
                this._IconBitmaps[iconName] = bitmap;
                info.Icon = bitmap;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async Task RunIconWorkerAsync()
        {
            while (true)
            {
                await this._IconSignal.WaitAsync();

                if (this._IconQueue.TryDequeue(out var info) == false)
                {
                    continue;
                }

                await this.LoadIconAsync(info);

                // Counted here rather than inside LoadIconAsync so that every
                // way of finishing an item -- no icon, already decoded, read
                // from disk, downloaded, or failed -- advances the bar by one.
                this._IconsCompleted++;
                if (this._IconQueue.IsEmpty == true)
                {
                    this.EndProgress();
                    this._IconsQueued = 0;
                    this._IconsCompleted = 0;
                    continue;
                }

                this.ReportProgress(
                    $"Loading achievement icons, {this._IconsCompleted:N0} of {this._IconsQueued:N0}...",
                    this._IconsCompleted,
                    this._IconsQueued);
            }
        }

        private async Task LoadIconAsync(Stats.AchievementInfo info)
        {
            var iconName = info.CurrentIconName;
            if (string.IsNullOrEmpty(iconName) == true)
            {
                return;
            }

            if (this._IconBitmaps.TryGetValue(iconName, out var cached) == true)
            {
                info.Icon = cached;
                return;
            }

            // Disk before network: a warm cache means no request at all.
            var icons = this._IconCache;
            var appId = (uint)this._GameId;
            var stored = await Task.Run(() => icons.TryRead(appId, iconName));
            if (stored != null && this.TryDecodeIcon(iconName, stored, info) == true)
            {
                return;
            }

            try
            {
                var url = _($"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{this._GameId}/{iconName}");
                var data = await _Http.GetByteArrayAsync(new Uri(url));
                if (this.TryDecodeIcon(iconName, data, info) == true)
                {
                    await Task.Run(() => icons.Write(appId, iconName, data));
                }
            }
            catch (Exception)
            {
                // A missing icon is not worth surfacing.
            }
        }

        #endregion
    }
}
