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
        private GameStats? _Stats;

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
