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
using static SAM.Picker.InvariantShorthand;
using APITypes = SAM.API.Types;

namespace SAM.Picker.ViewModels
{
    internal sealed partial class GamePickerViewModel : ObservableObject
    {
        private static readonly HttpClient _Http = new();

        private readonly API.Client _SteamClient;
        private readonly API.Callbacks.AppDataChanged _AppDataChangedCallback;
        private readonly DispatcherTimer _CallbackTimer;

        private readonly Dictionary<uint, GameInfo> _Games = new();
        private readonly HashSet<string> _LogosAttempted = new();
        private readonly ConcurrentQueue<GameInfo> _LogoQueue = new();
        private readonly SemaphoreSlim _LogoSignal = new(0);

        public ObservableCollection<GameInfo> FilteredGames { get; } = new();

        [ObservableProperty]
        private GameInfo _SelectedGame;

        [ObservableProperty]
        private string _StatusText = "";

        [ObservableProperty]
        private string _DownloadStatusText = "";

        [ObservableProperty]
        private bool _IsDownloadStatusVisible;

        [ObservableProperty]
        private string _AddGameText = "";

        [ObservableProperty]
        private bool _CanRefresh = true;

        private readonly LibraryStats _LibraryStats;

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
            _ => "Name",
        };

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

            if (this.SortField == GameSortField.OwnRating)
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

            // The WinForms build pumped callbacks from a Forms.Timer; the
            // dispatcher timer is the direct equivalent and keeps callbacks on
            // the UI thread, which the rest of this class assumes.
            this._CallbackTimer = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, this.OnTimer);
            this._CallbackTimer.Start();

            // Discards are spelled out here because `using static
            // InvariantShorthand` puts a method named `_` in scope.
            Task logoWorker = this.RunLogoWorkerAsync();
            Task initialLoad = this.LoadGamesAsync();
            GC.KeepAlive((logoWorker, initialLoad));
        }

        public void Shutdown()
        {
            this._CallbackTimer.Stop();
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
            await this.LoadGamesAsync();
        }

        private async Task LoadGamesAsync()
        {
            this.CanRefresh = false;
            this.StatusText = "Downloading game list...";

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
                this.ErrorRaised?.Invoke(e.ToString());
                return;
            }

            this.StatusText = "Checking game ownership...";

            // Ownership and name lookups are Steam client calls, one per app id
            // over the whole published list, so they cannot run on the UI
            // thread. The WinForms build ran them on a BackgroundWorker for the
            // same reason.
            var games = await Task.Run(() =>
            {
                Dictionary<uint, GameInfo> result = new();
                foreach (var kv in pairs)
                {
                    if (result.ContainsKey(kv.Key) == true)
                    {
                        continue;
                    }
                    if (this._SteamClient.SteamApps008.IsSubscribedApp(kv.Key) == false)
                    {
                        continue;
                    }
                    GameInfo info = new(kv.Key, kv.Value)
                    {
                        Name = this._SteamClient.SteamApps001.GetAppData(kv.Key, "name"),
                    };
                    result.Add(kv.Key, info);
                }
                return result;
            });

            this._Games.Clear();
            foreach (var kv in games)
            {
                this._Games.Add(kv.Key, kv.Value);
            }

            this.RefreshGames();
            this.CanRefresh = true;

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

            // Both halves are slow for a few hundred games: file I/O for the
            // caches, and two Steam calls per app for the store metadata.
            var loaded = await Task.Run(() =>
            {
                Dictionary<uint, (GameStats? Stats, int? Percent, int? Score, DateTime? Released)> result = new();
                foreach (var info in games)
                {
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

            if (this.SortField != GameSortField.Name)
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

            this._LogoQueue.Enqueue(info);
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

                this.DownloadStatusText = $"Downloading {1 + this._LogoQueue.Count} game icons...";
                this.IsDownloadStatusVisible = true;

                try
                {
                    var data = await _Http.GetByteArrayAsync(new Uri(info.ImageUrl));
                    using MemoryStream stream = new(data, false);
                    // Avalonia's Bitmap copies the decoded pixels, so unlike
                    // System.Drawing it does not need the stream kept alive.
                    info.Logo = new Bitmap(stream);
                }
                catch (Exception)
                {
                    // A missing capsule image is not worth surfacing.
                }

                if (this._LogoQueue.IsEmpty == true)
                {
                    this.IsDownloadStatusVisible = false;
                }
            }
        }

        #endregion
    }
}
