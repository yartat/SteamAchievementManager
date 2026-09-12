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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.XPath;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SAM.Shared;
using static SAM.Picker.InvariantShorthand;
using APITypes = SAM.API.Types;

namespace SAM.Picker.ViewModels
{
    internal sealed partial class GamePickerViewModel : ProgressViewModel
    {
        private static readonly HttpClient _Http = new();

        private readonly API.Client _SteamClient;
        private readonly API.Callbacks.AppDataChanged _AppDataChangedCallback;
        private readonly DispatcherTimer _CallbackTimer;

        private readonly Dictionary<uint, GameInfo> _Games = new();
        private readonly HashSet<string> _LogosAttempted = new();
        private readonly ConcurrentQueue<GameInfo> _LogoQueue = new();

        // How many have been queued and finished since the queue last drained,
        // so the bar has a denominator. UI thread only: EnqueueLogo is called
        // from it, and the worker's continuations return to it.
        private int _LogosQueued;
        private int _LogosCompleted;
        private readonly SemaphoreSlim _LogoSignal = new(0);

        public ObservableCollection<GameInfo> FilteredGames { get; } = new();

        [ObservableProperty]
        private GameInfo _SelectedGame;

        [ObservableProperty]
        private string _StatusText = "";

        [ObservableProperty]
        private string _AddGameText = "";

        [ObservableProperty]
        private bool _CanRefresh = true;

        private readonly LibraryStats _LibraryStats;

        private readonly AppSettings _Settings;
        private GameCache _Cache;
        private IconCache _IconCache;
        private string _CacheError;

        private bool _IsContentView;
        /// <summary>
        /// False shows the tile grid (large capsule + name), true shows one row
        /// per game with playtime and achievement counts.
        /// </summary>
        public bool IsContentView
        {
            get => this._IsContentView;
            set
            {
                if (this.SetProperty(ref this._IsContentView, value) == true)
                {
                    this.OnPropertyChanged(nameof(this.IsTileView));
                }
            }
        }

        public bool IsTileView
        {
            get => this._IsContentView == false;
            set => this.IsContentView = value == false;
        }

        [RelayCommand]
        private void ShowTiles()
        {
            this.IsContentView = false;
        }

        [RelayCommand]
        private void ShowContent()
        {
            this.IsContentView = true;
        }

        #region Sorting

        private readonly RatingStore _RatingStore = RatingStore.Load();

        private GameSortField _SortField = GameSortField.Name;
        public GameSortField SortField
        {
            get => this._SortField;
            set
            {
                if (this.SetProperty(ref this._SortField, value) == true)
                {
                    this.OnPropertyChanged(nameof(this.SortDescriptionText));
                    this.RefreshGames();
                }
            }
        }

        private bool _SortDescending;
        public bool SortDescending
        {
            get => this._SortDescending;
            set
            {
                if (this.SetProperty(ref this._SortDescending, value) == true)
                {
                    this.OnPropertyChanged(nameof(this.SortDescriptionText));
                    this.RefreshGames();
                }
            }
        }

        public string SortDescriptionText => this.SortField switch
        {
            GameSortField.ReleaseDate => "Released",
            GameSortField.LastPlayed => "Last played",
            GameSortField.SteamRating => "Steam rating",
            GameSortField.OwnRating => "My rating",
            GameSortField.Completion => "Completion",
            _ => "Name",
        };

        private GameCollection _SelectedCollection = GameCollection.All;

        /// <summary>
        /// Which of the rail's collections is showing. Unlike the type toggles
        /// below, these are mutually exclusive.
        /// </summary>
        public GameCollection SelectedCollection
        {
            get => this._SelectedCollection;
            set
            {
                if (this.SetProperty(ref this._SelectedCollection, value) == false)
                {
                    return;
                }
                this.OnPropertyChanged(nameof(this.CollectionTitle));
                foreach (var name in CollectionSelectionNames)
                {
                    this.OnPropertyChanged(name);
                }
                this.RefreshGames();
            }
        }

        private static readonly string[] CollectionSelectionNames =
        {
            nameof(IsAllSelected), nameof(IsRecentSelected), nameof(IsWithAchievementsSelected),
            nameof(IsPerfectSelected), nameof(IsLikedSelected),
        };

        public bool IsAllSelected => this.SelectedCollection == GameCollection.All;
        public bool IsRecentSelected => this.SelectedCollection == GameCollection.RecentlyPlayed;
        public bool IsWithAchievementsSelected => this.SelectedCollection == GameCollection.WithAchievements;
        public bool IsPerfectSelected => this.SelectedCollection == GameCollection.Perfect;
        public bool IsLikedSelected => this.SelectedCollection == GameCollection.Liked;

        public string CollectionTitle => this.SelectedCollection switch
        {
            GameCollection.RecentlyPlayed => "Recently played",
            GameCollection.WithAchievements => "Has achievements",
            GameCollection.Perfect => "Perfect games",
            GameCollection.Liked => "Liked",
            _ => "All games",
        };

        [RelayCommand]
        private void SelectCollection(GameCollection collection)
        {
            this.SelectedCollection = collection;
        }

        /// <summary>
        /// The rail's counts. Deliberately computed over the whole library
        /// rather than the filtered view, so a count never changes just because
        /// a different collection is open.
        /// </summary>
        public int AllCount => this._Games.Count;

        public int RecentCount => this._Games.Values.Count(g => g.LastPlayed.HasValue == true);

        public int WithAchievementsCount => this._Games.Values.Count(g => g.HasCompletion == true);

        public int PerfectCount => this._Games.Values.Count(g => g.IsPerfect == true);

        public int LikedCount => this._Games.Values.Count(g => g.IsLiked == true);

        /// <summary>Earned and total across everything Steam has cached.</summary>
        public string LibrarySummaryText
        {
            get
            {
                var earned = 0;
                var total = 0;
                foreach (var info in this._Games.Values)
                {
                    if (info.Stats.HasValue == false || info.Stats.Value.AchievementsTotal <= 0)
                    {
                        continue;
                    }
                    earned += info.Stats.Value.AchievementsEarned;
                    total += info.Stats.Value.AchievementsTotal;
                }
                return total == 0
                    ? $"{this._Games.Count:N0} owned"
                    : $"{this._Games.Count:N0} owned · {earned:N0} of {total:N0} achievements";
            }
        }

        private static readonly string[] CollectionCountNames =
        {
            nameof(AllCount), nameof(RecentCount), nameof(WithAchievementsCount),
            nameof(PerfectCount), nameof(LikedCount), nameof(LibrarySummaryText),
        };

        /// <summary>Call after anything that changes the library or its stats.</summary>
        private void RefreshCollectionCounts()
        {
            foreach (var name in CollectionCountNames)
            {
                this.OnPropertyChanged(name);
            }
        }

        /// <summary>
        /// Clicking the field already being sorted by flips the direction,
        /// which is what a list header is expected to do.
        /// </summary>
        [RelayCommand]
        private void SortBy(GameSortField field)
        {
            if (this.SortField == field)
            {
                this.SortDescending = this.SortDescending == false;
                return;
            }
            // Dates and ratings are far more useful highest-first.
            this._SortDescending = field != GameSortField.Name;
            this.OnPropertyChanged(nameof(this.SortDescending));
            this.SortField = field;
        }

        [RelayCommand]
        private void ToggleSortDirection()
        {
            this.SortDescending = this.SortDescending == false;
        }

        private IEnumerable<GameInfo> ApplySort(IEnumerable<GameInfo> games)
        {
            // Name is always the tie-breaker so the order is stable and
            // predictable when a key is missing for many games.
            return this.SortField switch
            {
                GameSortField.ReleaseDate => this.SortDescending == true
                    ? games.OrderByDescending(g => g.ReleaseDate ?? DateTime.MinValue).ThenBy(g => g.Name)
                    : games.OrderBy(g => g.ReleaseDate ?? DateTime.MaxValue).ThenBy(g => g.Name),
                GameSortField.LastPlayed => this.SortDescending == true
                    ? games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Name)
                    : games.OrderBy(g => g.LastPlayed ?? DateTime.MaxValue).ThenBy(g => g.Name),
                GameSortField.SteamRating => this.SortDescending == true
                    ? games.OrderByDescending(g => g.SteamRatingPercent ?? -1).ThenBy(g => g.Name)
                    : games.OrderBy(g => g.SteamRatingPercent ?? int.MaxValue).ThenBy(g => g.Name),
                GameSortField.OwnRating => this.SortDescending == true
                    ? games.OrderByDescending(g => RatingOrder(g.OwnRating)).ThenBy(g => g.Name)
                    : games.OrderBy(g => RatingOrder(g.OwnRating)).ThenBy(g => g.Name),
                // Games Steam has never cached sort to the far end either way
                // rather than pretending to be 0%.
                GameSortField.Completion => this.SortDescending == true
                    ? games.OrderByDescending(g => g.Completion ?? -1.0).ThenBy(g => g.Name)
                    : games.OrderBy(g => g.Completion ?? 2.0).ThenBy(g => g.Name),
                _ => this.SortDescending == true
                    ? games.OrderByDescending(g => g.Name)
                    : games.OrderBy(g => g.Name),
            };
        }

        private static int RatingOrder(OwnRating rating) => rating switch
        {
            OwnRating.Like => 2,
            OwnRating.None => 1,
            OwnRating.Dislike => 0,
            _ => 1,
        };

        #endregion

        #region Settings

        /// <summary>
        /// Asks the view to show the settings dialog; returns the accepted
        /// settings, or null if the user cancelled.
        /// </summary>
        public event Func<AppSettings, Task<AppSettings>> SettingsRequested;

        [RelayCommand]
        private async Task OpenSettingsAsync()
        {
            if (this.SettingsRequested == null)
            {
                return;
            }

            var updated = await this.SettingsRequested(this._Settings.Clone());
            if (updated == null)
            {
                return;
            }

            await this.ApplySettingsAsync(updated);
        }

        /// <summary>
        /// Applies new cache locations, moving the existing data across. The
        /// database is closed first because SQLite keeps its WAL sidecars open.
        /// </summary>
        private async Task ApplySettingsAsync(AppSettings updated)
        {
            var databaseMoved = string.Equals(
                updated.DatabasePath, this._Settings.DatabasePath, StringComparison.OrdinalIgnoreCase) == false;
            var iconsMoved = string.Equals(
                updated.IconCachePath, this._Settings.IconCachePath, StringComparison.OrdinalIgnoreCase) == false;

            if (databaseMoved == false && iconsMoved == false)
            {
                return;
            }

            try
            {
                if (databaseMoved == true && this._Cache != null)
                {
                    var cache = this._Cache;
                    var destination = updated.DatabasePath;
                    this._Cache = await Task.Run(() => GameCache.MoveTo(cache, destination));
                }
                else if (databaseMoved == true)
                {
                    this._Settings.DatabasePath = updated.DatabasePath;
                    this.OpenCache();
                }

                if (iconsMoved == true)
                {
                    var icons = this._IconCache;
                    var destination = updated.IconCachePath;
                    await Task.Run(() => icons.MoveTo(destination));
                }
            }
            catch (Exception e)
            {
                this.ErrorRaised?.Invoke("Could not move the cache to the new location:\n" + e.Message);
                return;
            }

            this._Settings.DatabasePath = updated.DatabasePath;
            this._Settings.IconCachePath = updated.IconCachePath;

            if (this._Settings.Save() == false)
            {
                this.ErrorRaised?.Invoke("The new locations are in use for this session but could not be saved.");
            }
        }

        #endregion

        #region Own rating

        [RelayCommand]
        private void ToggleLike(GameInfo info)
        {
            this.SetOwnRating(info, info?.OwnRating == OwnRating.Like ? OwnRating.None : OwnRating.Like);
        }

        [RelayCommand]
        private void ToggleDislike(GameInfo info)
        {
            this.SetOwnRating(info, info?.OwnRating == OwnRating.Dislike ? OwnRating.None : OwnRating.Dislike);
        }

        private void SetOwnRating(GameInfo info, OwnRating rating)
        {
            if (info == null)
            {
                return;
            }

            info.OwnRating = rating;

            if (this._RatingStore.Set(info.Id, rating) == false)
            {
                this.ErrorRaised?.Invoke(
                    "Your rating was applied for this session but could not be saved to disk.");
            }

            this.RefreshCollectionCounts();

            if (this.SortField == GameSortField.OwnRating ||
                this.SelectedCollection == GameCollection.Liked)
            {
                this.RefreshGames();
            }
        }

        #endregion

        private string _SearchText = "";
        public string SearchText
        {
            get => this._SearchText;
            set
            {
                if (this.SetProperty(ref this._SearchText, value) == true)
                {
                    this.RefreshGames();
                }
            }
        }

        private bool _ShowGames = true;
        public bool ShowGames
        {
            get => this._ShowGames;
            set => this.SetFilter(ref this._ShowGames, value);
        }

        private bool _ShowDemos;
        public bool ShowDemos
        {
            get => this._ShowDemos;
            set => this.SetFilter(ref this._ShowDemos, value);
        }

        private bool _ShowMods;
        public bool ShowMods
        {
            get => this._ShowMods;
            set => this.SetFilter(ref this._ShowMods, value);
        }

        private bool _ShowJunk;
        public bool ShowJunk
        {
            get => this._ShowJunk;
            set => this.SetFilter(ref this._ShowJunk, value);
        }

        private void SetFilter(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            if (this.SetProperty(ref field, value, name) == true)
            {
                this.RefreshGames();
            }
        }

        /// <summary>
        /// Raised when something needs to be shown to the user; the view owns
        /// the actual dialog because the view model has no window to parent to.
        /// </summary>
        public event Action<string> ErrorRaised;

        public GamePickerViewModel(API.Client client)
        {
            this._SteamClient = client;

            this._AppDataChangedCallback = client.CreateAndRegisterCallback<API.Callbacks.AppDataChanged>();
            this._AppDataChangedCallback.OnRun += this.OnAppDataChanged;

            this._LibraryStats = LibraryStats.Create(client.SteamUser.GetSteamId());

            this._Settings = AppSettings.Load();
            this._IconCache = new(this._Settings.IconCachePath);
            this.OpenCache();

            // The WinForms build pumped callbacks from a Forms.Timer; the
            // dispatcher timer is the direct equivalent and keeps callbacks on
            // the UI thread, which the rest of this class assumes.
            this._CallbackTimer = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, this.OnTimer);
            this._CallbackTimer.Start();

            // Cached rows are loaded synchronously so the window has content
            // the moment it is shown; everything that touches the network or
            // Steam happens afterwards, in the background.
            this.LoadFromCache();

            // Discards are spelled out here because `using static
            // InvariantShorthand` puts a method named `_` in scope.
            Task logoWorker = this.RunLogoWorkerAsync();
            Task icons = this.HydrateCachedIconsAsync();
            Task initialLoad = this.RefreshLibraryAsync();
            GC.KeepAlive((logoWorker, icons, initialLoad));
        }

        private void OpenCache()
        {
            try
            {
                this._Cache = GameCache.Open(this._Settings.DatabasePath);
            }
            catch (Exception e)
            {
                // A cache that will not open must not stop the app; it just
                // means every start is a cold one.
                this._Cache = null;
                this._CacheError = e.Message;
            }
        }

        /// <summary>
        /// Paints the window from the database before any network or Steam work
        /// happens. Everything here is already-persisted data.
        /// </summary>
        private void LoadFromCache()
        {
            if (this._Cache == null)
            {
                this.StatusText = this._CacheError == null
                    ? "Loading games..."
                    : $"Cache unavailable ({this._CacheError}); loading from Steam...";
                return;
            }

            List<CachedGame> rows;
            try
            {
                rows = this._Cache.LoadAll();
            }
            catch (Exception)
            {
                return;
            }

            foreach (var row in rows)
            {
                GameInfo info = new(row.Id, row.Type ?? "normal")
                {
                    Name = row.Name,
                    ImageUrl = row.ImageUrl,
                    ReleaseDate = row.ReleaseDate,
                    SteamRatingPercent = row.RatingPercent,
                    SteamRatingScore = row.RatingScore,
                    OwnRating = this._RatingStore.Get(row.Id),
                };
                if (row.HasStats == true)
                {
                    info.Stats = new GameStats(
                        row.PlaytimeMinutes,
                        row.AchievementsTotal,
                        row.AchievementsEarned,
                        row.LastPlayed);
                }
                this._Games[row.Id] = info;
            }

            if (this._Games.Count > 0)
            {
                this.RefreshCollectionCounts();
                this.RefreshGames();
                this.StatusText = $"Showing {this._Games.Count} cached games. Refreshing...";
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

        private void OnAppDataChanged(APITypes.AppDataChanged param)
        {
            if (param.Result == false)
            {
                return;
            }
            if (this._Games.TryGetValue(param.Id, out var info) == false)
            {
                return;
            }
            info.Name = this._SteamClient.SteamApps001.GetAppData(info.Id, "name");
            this.EnqueueLogo(info);
        }

        #region Game list

        [RelayCommand]
        private async Task RefreshAsync()
        {
            this.AddGameText = "";
            await this.RefreshLibraryAsync();
        }

        /// <summary>
        /// Brings the cache up to date: re-reads the published list, re-checks
        /// ownership, refreshes stats, then writes the result back so the next
        /// start is instant. Runs after the cached view is already on screen.
        /// </summary>
        private async Task RefreshLibraryAsync()
        {
            await this.LoadGamesAsync();
            await this.SaveCacheAsync();
        }

        private async Task SaveCacheAsync()
        {
            if (this._Cache == null)
            {
                return;
            }

            var rows = this._Games.Values.Select(info => new CachedGame()
            {
                Id = info.Id,
                Type = info.Type,
                Name = info.Name,
                ImageUrl = info.ImageUrl,
                ReleaseDate = info.ReleaseDate,
                RatingPercent = info.SteamRatingPercent,
                RatingScore = info.SteamRatingScore,
                PlaytimeMinutes = info.Stats?.PlaytimeMinutes ?? 0,
                LastPlayed = info.Stats?.LastPlayed,
                AchievementsTotal = info.Stats?.AchievementsTotal ?? -1,
                AchievementsEarned = info.Stats?.AchievementsEarned ?? -1,
                HasStats = info.Stats.HasValue,
            }).ToList();

            var owned = this._Games.Keys.ToHashSet();
            var cache = this._Cache;
            var icons = this._IconCache;

            try
            {
                // Rows for games no longer owned are deleted here, and their
                // cached capsules go with them.
                await Task.Run(() =>
                {
                    cache.Sync(rows);
                    icons.Prune(owned);
                });
            }
            catch (Exception e)
            {
                this.ErrorRaised?.Invoke("Could not update the game cache:\n" + e.Message);
            }
        }

        /// <summary>
        /// Decodes capsules already on disk, off the UI thread, so a warm start
        /// shows art without touching the network.
        /// </summary>
        private async Task HydrateCachedIconsAsync()
        {
            var games = this._Games.Values.Where(g => g.Logo == null).ToList();
            if (games.Count == 0)
            {
                return;
            }

            var icons = this._IconCache;
            var total = games.Count;
            var step = ProgressStep(total);
            IProgress<int> decode = new Progress<int>(
                done => this.ReportProgress($"Loading cached icons, {done:N0} of {total:N0}...", done, total));
            this.BeginProgress("Loading cached icons...");
            this.ReportProgress($"Loading cached icons, 0 of {total:N0}...", 0, total);

            var decoded = await Task.Run(() =>
            {
                Dictionary<uint, Bitmap> result = new();
                var done = 0;
                foreach (var info in games)
                {
                    done++;
                    if (done % step == 0)
                    {
                        decode.Report(done);
                    }
                    var bytes = icons.TryRead(info.Id);
                    if (bytes == null)
                    {
                        continue;
                    }
                    try
                    {
                        using MemoryStream stream = new(bytes, false);
                        result[info.Id] = new Bitmap(stream);
                    }
                    catch (Exception)
                    {
                        // A truncated cache file just means a re-download.
                    }
                }
                return result;
            });

            foreach (var info in games)
            {
                if (decoded.TryGetValue(info.Id, out var bitmap) == true)
                {
                    info.Logo = bitmap;
                }
            }

            this.EndProgress();
        }

        private async Task LoadGamesAsync()
        {
            this.CanRefresh = false;
            this.StatusText = "Downloading game list...";
            this.BeginProgress("Downloading game list...");

            List<KeyValuePair<uint, string>> pairs;
            try
            {
                var bytes = await _Http.GetByteArrayAsync(new Uri("https://gib.me/sam/games.xml"));
                pairs = ParseGameList(bytes);
            }
            catch (Exception e)
            {
                this._Games.Clear();
                this.AddGame(480, "normal"); // Spacewar
                this.RefreshGames();
                this.CanRefresh = true;
                this.EndProgress();
                this.ErrorRaised?.Invoke(e.ToString());
                return;
            }

            this.StatusText = "Checking game ownership...";

            // Created here, on the UI thread, so its callback marshals back to
            // the UI thread from inside the Task.Run below.
            var total = pairs.Count;
            var step = ProgressStep(total);
            IProgress<int> sweep = new Progress<int>(
                done => this.ReportProgress($"Checking ownership, {done:N0} of {total:N0}...", done, total));
            this.ReportProgress($"Checking ownership, 0 of {total:N0}...", 0, total);

            // Ownership and name lookups are Steam client calls, one per app id
            // over the whole published list, so they cannot run on the UI
            // thread. The WinForms build ran them on a BackgroundWorker for the
            // same reason.
            var owned = await Task.Run(() =>
            {
                Dictionary<uint, (string Type, string Name)> result = new();
                var examined = 0;
                foreach (var kv in pairs)
                {
                    examined++;
                    if (examined % step == 0)
                    {
                        sweep.Report(examined);
                    }
                    if (result.ContainsKey(kv.Key) == true)
                    {
                        continue;
                    }
                    if (this._SteamClient.SteamApps008.IsSubscribedApp(kv.Key) == false)
                    {
                        continue;
                    }
                    result.Add(kv.Key, (kv.Value, this._SteamClient.SteamApps001.GetAppData(kv.Key, "name")));
                }
                return result;
            });

            // Merge rather than rebuild: the cached entries already on screen
            // carry decoded capsules and the user's own rating, and replacing
            // them wholesale would blank the window and re-download everything.
            foreach (var id in this._Games.Keys.Where(id => owned.ContainsKey(id) == false).ToList())
            {
                this._Games.Remove(id);
            }

            foreach (var kv in owned)
            {
                if (this._Games.TryGetValue(kv.Key, out var existing) == true)
                {
                    existing.Name = kv.Value.Name;
                    continue;
                }
                this._Games[kv.Key] = new GameInfo(kv.Key, kv.Value.Type)
                {
                    Name = kv.Value.Name,
                    OwnRating = this._RatingStore.Get(kv.Key),
                };
            }

            this.RefreshCollectionCounts();
            this.RefreshGames();
            this.CanRefresh = true;
            this.EndProgress();

            await this.LoadLibraryStatsAsync();
        }

        /// <summary>
        /// Reads playtime and achievement counts for every owned game off disk.
        /// Done eagerly in the background rather than on first switch to the
        /// content view, so the view has data the moment it is shown. It is
        /// file I/O over a few hundred small files, so it stays off the UI
        /// thread.
        /// </summary>
        private async Task LoadLibraryStatsAsync()
        {
            var games = this._Games.Values.ToList();

            // Own ratings are already in memory; apply them before anything
            // that might sort on them.
            foreach (var info in games)
            {
                info.OwnRating = this._RatingStore.Get(info.Id);
            }

            var total = games.Count;
            var step = ProgressStep(total);
            IProgress<int> pass = new Progress<int>(
                done => this.ReportProgress($"Reading library stats, {done:N0} of {total:N0}...", done, total));
            this.BeginProgress("Reading library stats...");
            this.ReportProgress($"Reading library stats, 0 of {total:N0}...", 0, total);

            // Both halves are slow for a few hundred games: file I/O for the
            // caches, and two Steam calls per app for the store metadata.
            var loaded = await Task.Run(() =>
            {
                Dictionary<uint, (GameStats? Stats, int? Percent, int? Score, DateTime? Released)> result = new();
                var done = 0;
                foreach (var info in games)
                {
                    done++;
                    if (done % step == 0)
                    {
                        pass.Report(done);
                    }
                    var stats = this._LibraryStats?.TryGet(info.Id);
                    result[info.Id] = (
                        stats,
                        ParseInt(this._SteamClient.SteamApps001.GetAppData(info.Id, "review_percentage")),
                        ParseInt(this._SteamClient.SteamApps001.GetAppData(info.Id, "review_score")),
                        ParseUnixDate(this._SteamClient.SteamApps001.GetAppData(info.Id, "steam_release_date")));
                }
                return result;
            });

            foreach (var info in games)
            {
                if (loaded.TryGetValue(info.Id, out var value) == false)
                {
                    continue;
                }
                info.Stats = value.Stats;
                info.SteamRatingPercent = value.Percent;
                info.SteamRatingScore = value.Score;
                info.ReleaseDate = value.Released;
            }

            this.EndProgress();
            this.RefreshCollectionCounts();

            // Completion only becomes known here, so a collection or sort that
            // depends on it has to be re-evaluated.
            if (this.SortField != GameSortField.Name ||
                this.SelectedCollection != GameCollection.All)
            {
                this.RefreshGames();
            }
        }

        private static int? ParseInt(string value)
        {
            return string.IsNullOrEmpty(value) == false &&
                   int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static DateTime? ParseUnixDate(string value)
        {
            return string.IsNullOrEmpty(value) == false &&
                   long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
                   seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
                : null;
        }

        private static List<KeyValuePair<uint, string>> ParseGameList(byte[] bytes)
        {
            List<KeyValuePair<uint, string>> pairs = new();
            using MemoryStream stream = new(bytes, false);
            XPathDocument document = new(stream);
            var navigator = document.CreateNavigator();
            var nodes = navigator.Select("/games/game");
            while (nodes.MoveNext() == true)
            {
                var type = nodes.Current.GetAttribute("type", "");
                if (string.IsNullOrEmpty(type) == true)
                {
                    type = "normal";
                }
                pairs.Add(new((uint)nodes.Current.ValueAsLong, type));
            }
            return pairs;
        }

        private void AddGame(uint id, string type)
        {
            if (this._Games.ContainsKey(id) == true)
            {
                return;
            }
            if (this._SteamClient.SteamApps008.IsSubscribedApp(id) == false)
            {
                return;
            }
            GameInfo info = new(id, type)
            {
                Name = this._SteamClient.SteamApps001.GetAppData(id, "name"),
            };
            this._Games.Add(id, info);
        }

        private void RefreshGames()
        {
            var nameSearch = this.SearchText.Length > 0 ? this.SearchText : null;

            this.FilteredGames.Clear();
            foreach (var info in this.ApplySort(this._Games.Values))
            {
                if (nameSearch != null &&
                    info.Name.IndexOf(nameSearch, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool wanted = info.Type switch
                {
                    "normal" => this.ShowGames,
                    "demo" => this.ShowDemos,
                    "mod" => this.ShowMods,
                    "junk" => this.ShowJunk,
                    _ => true,
                };
                if (wanted == false)
                {
                    continue;
                }

                var inCollection = this.SelectedCollection switch
                {
                    GameCollection.RecentlyPlayed => info.LastPlayed.HasValue,
                    GameCollection.WithAchievements => info.HasCompletion,
                    GameCollection.Perfect => info.IsPerfect,
                    GameCollection.Liked => info.IsLiked,
                    _ => true,
                };
                if (inCollection == false)
                {
                    continue;
                }

                this.FilteredGames.Add(info);
            }

            this.StatusText =
                $"Displaying {this.FilteredGames.Count} games. Total {this._Games.Count} games.";

            if (this.FilteredGames.Count > 0)
            {
                this.SelectedGame = this.FilteredGames[0];
            }

            foreach (var info in this.FilteredGames)
            {
                this.EnqueueLogo(info);
            }
        }

        [RelayCommand]
        private void AddGameById()
        {
            if (uint.TryParse(this.AddGameText, out var id) == false)
            {
                this.ErrorRaised?.Invoke("Please enter a valid game ID.");
                return;
            }

            if (this._SteamClient.SteamApps008.IsSubscribedApp(id) == false)
            {
                this.ErrorRaised?.Invoke("You don't own that game.");
                return;
            }

            while (this._LogoQueue.TryDequeue(out var discarded) == true)
            {
                // Clear the queue; only one app is about to be shown.
                this._LogosAttempted.Remove(discarded.ImageUrl);
            }

            this.AddGameText = "";
            this._Games.Clear();
            this.AddGame(id, "normal");
            this.ShowGames = true;
            this.RefreshGames();
        }

        [RelayCommand]
        private void LaunchSelected()
        {
            var info = this.SelectedGame;
            if (info == null)
            {
                return;
            }

            try
            {
                // The apphost has no extension outside Windows.
                var manager = OperatingSystem.IsWindows() == true ? "SAM.Game.exe" : "SAM.Game";
                Process.Start(
                    Path.Combine(AppContext.BaseDirectory, manager),
                    info.Id.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                this.ErrorRaised?.Invoke("Failed to launch SAM.Game.exe.");
            }
        }

        #endregion

        #region Logos

        private string GetGameImageUrl(uint id)
        {
            var currentLanguage = this._SteamClient.SteamApps008.GetCurrentGameLanguage();

            var candidate = this._SteamClient.SteamApps001.GetAppData(id, _($"small_capsule/{currentLanguage}"));
            if (string.IsNullOrEmpty(candidate) == false)
            {
                return _($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/{candidate}");
            }

            if (currentLanguage != "english")
            {
                candidate = this._SteamClient.SteamApps001.GetAppData(id, "small_capsule/english");
                if (string.IsNullOrEmpty(candidate) == false)
                {
                    return _($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/{candidate}");
                }
            }

            candidate = this._SteamClient.SteamApps001.GetAppData(id, "logo");
            if (string.IsNullOrEmpty(candidate) == false)
            {
                return _($"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{id}/{candidate}.jpg");
            }

            return null;
        }

        private void EnqueueLogo(GameInfo info)
        {
            if (info.Logo != null)
            {
                return;
            }

            var imageUrl = this.GetGameImageUrl(info.Id);
            if (string.IsNullOrEmpty(imageUrl) == true)
            {
                return;
            }

            info.ImageUrl = imageUrl;

            if (this._LogosAttempted.Add(imageUrl) == false)
            {
                return;
            }

            if (this._LogosQueued == 0)
            {
                this.BeginProgress("Loading game icons...");
            }

            this._LogoQueue.Enqueue(info);
            this._LogosQueued++;
            this._LogoSignal.Release();
        }

        /// <summary>
        /// Single consumer, one download at a time — same shape as the
        /// BackgroundWorker the WinForms build used.
        /// </summary>
        private async Task RunLogoWorkerAsync()
        {
            while (true)
            {
                await this._LogoSignal.WaitAsync();

                if (this._LogoQueue.TryDequeue(out var info) == false)
                {
                    continue;
                }

                await this.LoadLogoAsync(info);

                // Counted here rather than inside LoadLogoAsync so that every
                // way of finishing an item -- already loaded, read from disk,
                // downloaded, or failed -- advances the bar by exactly one.
                this._LogosCompleted++;
                if (this._LogoQueue.IsEmpty == true)
                {
                    this.EndProgress();
                    this._LogosQueued = 0;
                    this._LogosCompleted = 0;
                    continue;
                }

                this.ReportProgress(
                    $"Loading game icons, {this._LogosCompleted:N0} of {this._LogosQueued:N0}...",
                    this._LogosCompleted,
                    this._LogosQueued);
            }
        }

        private async Task LoadLogoAsync(GameInfo info)
        {
            if (info.Logo != null)
            {
                // Hydrated from the disk cache while this was queued.
                return;
            }

            // Disk before network: a warm cache means no request at all.
            var icons = this._IconCache;
            var appId = info.Id;
            var cached = await Task.Run(() => icons.TryRead(appId));
            if (cached != null)
            {
                try
                {
                    using MemoryStream stream = new(cached, false);
                    info.Logo = new Bitmap(stream);
                    return;
                }
                catch (Exception)
                {
                    // Fall through and re-download a corrupt cache entry.
                }
            }

            try
            {
                var data = await _Http.GetByteArrayAsync(new Uri(info.ImageUrl));
                using MemoryStream stream = new(data, false);
                // Avalonia's Bitmap copies the decoded pixels, so unlike
                // System.Drawing it does not need the stream kept alive.
                info.Logo = new Bitmap(stream);
                await Task.Run(() => icons.Write(appId, data));
            }
            catch (Exception)
            {
                // A missing capsule image is not worth surfacing.
            }
        }

        #endregion
    }
}
