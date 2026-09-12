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
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SAM.Picker
{
    internal sealed partial class GameInfo : ObservableObject
    {
        private string _Name;

        public uint Id { get; }
        public string Type { get; }
        public string ImageUrl { get; set; }

        /// <summary>
        /// Filled in asynchronously by the logo downloader; the view binds to
        /// it directly, so the assignment must happen on the UI thread.
        /// </summary>
        [ObservableProperty]
        private Bitmap _Logo;

        public string Name
        {
            get => this._Name;
            set => this.SetProperty(
                ref this._Name,
                value ?? "App " + this.Id.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Filled in from Steam's on-disk caches by <see cref="LibraryStats"/>.
        /// Null means "not known" — Steam only writes those caches for games
        /// that have been launched — which is deliberately distinct from zero.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlaytimeText))]
        [NotifyPropertyChangedFor(nameof(AchievementsText))]
        [NotifyPropertyChangedFor(nameof(LastPlayedText))]
        [NotifyPropertyChangedFor(nameof(Completion))]
        [NotifyPropertyChangedFor(nameof(HasCompletion))]
        [NotifyPropertyChangedFor(nameof(CompletionFraction))]
        [NotifyPropertyChangedFor(nameof(CompletionText))]
        [NotifyPropertyChangedFor(nameof(IsPerfect))]
        private GameStats? _Stats;

        /// <summary>Steam's community review score as a percentage positive.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SteamRatingText))]
        [NotifyPropertyChangedFor(nameof(SteamRatingTooltip))]
        private int? _SteamRatingPercent;

        /// <summary>Steam's 1-9 review band, used for the descriptive tooltip.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SteamRatingTooltip))]
        private int? _SteamRatingScore;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ReleaseDateText))]
        private DateTime? _ReleaseDate;

        /// <summary>
        /// The user's own like/dislike. SAM-local; see <see cref="OwnRating"/>.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLiked))]
        [NotifyPropertyChangedFor(nameof(IsDisliked))]
        private OwnRating _OwnRating;

        public bool IsLiked => this.OwnRating == OwnRating.Like;
        public bool IsDisliked => this.OwnRating == OwnRating.Dislike;

        public DateTime? LastPlayed => this.Stats?.LastPlayed;

        public string LastPlayedText =>
            this.Stats.HasValue == true && this.Stats.Value.LastPlayed.HasValue == true
                ? this.Stats.Value.LastPlayed.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
                : "—";

        public string ReleaseDateText =>
            this.ReleaseDate.HasValue == true
                ? this.ReleaseDate.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
                : "—";

        public string SteamRatingText =>
            this.SteamRatingPercent.HasValue == true
                ? this.SteamRatingPercent.Value.ToString(CultureInfo.CurrentCulture) + "%"
                : "—";

        public string SteamRatingTooltip
        {
            get
            {
                if (this.SteamRatingPercent.HasValue == false)
                {
                    return "No Steam review data cached for this game";
                }
                var band = DescribeScore(this.SteamRatingScore);
                return band == null
                    ? $"{this.SteamRatingPercent.Value}% of Steam reviews are positive"
                    : $"{band} — {this.SteamRatingPercent.Value}% of Steam reviews are positive";
            }
        }

        /// <summary>Steam's published mapping for its 1-9 review score band.</summary>
        private static string DescribeScore(int? score) => score switch
        {
            9 => "Overwhelmingly Positive",
            8 => "Very Positive",
            7 => "Positive",
            6 => "Mostly Positive",
            5 => "Mixed",
            4 => "Mostly Negative",
            3 => "Negative",
            2 => "Very Negative",
            1 => "Overwhelmingly Negative",
            _ => null,
        };

        public string PlaytimeText
        {
            get
            {
                if (this.Stats.HasValue == false)
                {
                    return "—";
                }
                var minutes = this.Stats.Value.PlaytimeMinutes;
                if (minutes <= 0)
                {
                    return "never played";
                }
                var hours = minutes / 60.0;
                return hours < 10
                    ? hours.ToString("0.0", CultureInfo.CurrentCulture) + " h"
                    : Math.Round(hours).ToString("0", CultureInfo.CurrentCulture) + " h";
            }
        }

        /// <summary>
        /// How far through this game's achievements the user is, 0..1, or null
        /// when Steam has never cached a schema for it. The Library grid draws
        /// a meter from this, and an empty meter has to mean "not known" rather
        /// than "none earned".
        /// </summary>
        public double? Completion
        {
            get
            {
                if (this.Stats.HasValue == false || this.Stats.Value.AchievementsTotal <= 0)
                {
                    return null;
                }
                return Math.Clamp(
                    this.Stats.Value.AchievementsEarned / (double)this.Stats.Value.AchievementsTotal,
                    0.0,
                    1.0);
            }
        }

        public bool HasCompletion => this.Completion.HasValue;

        /// <summary>Width of the meter's filled part, as a fraction of the track.</summary>
        public double CompletionFraction => this.Completion ?? 0.0;

        public bool IsPerfect => this.Completion.HasValue == true && this.Completion.Value >= 1.0;

        public string CompletionText =>
            this.Completion.HasValue == false
                ? "Not cached yet"
                : (this.Completion.Value * 100.0).ToString("0", CultureInfo.CurrentCulture) + "%";

        public string AchievementsText
        {
            get
            {
                if (this.Stats.HasValue == false || this.Stats.Value.AchievementsTotal < 0)
                {
                    return "—";
                }
                if (this.Stats.Value.AchievementsTotal == 0)
                {
                    return "none";
                }
                return $"{this.Stats.Value.AchievementsEarned} / {this.Stats.Value.AchievementsTotal}";
            }
        }

        public GameInfo(uint id, string type)
        {
            this.Id = id;
            this.Type = type;
            this.Name = null;
            this.ImageUrl = null;
        }
    }
}
