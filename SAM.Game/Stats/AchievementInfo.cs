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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SAM.Game.Stats
{
    internal sealed partial class AchievementInfo : ObservableObject
    {
        public string Id { get; set; }
        public DateTime? UnlockTime { get; set; }
        public int Permission { get; set; }
        public string IconNormal { get; set; }
        public string IconLocked { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        /// <summary>
        /// True for achievements Steam will not let SAM manage. The view tints
        /// these and the view model refuses to toggle them.
        /// </summary>
        public bool IsProtected => (this.Permission & 3) != 0;

        /// <summary>
        /// The WinForms build painted protected rows dark red; keep that cue.
        /// Exposed as a brush rather than a converter to match how
        /// <see cref="Icon"/> is already handled.
        /// </summary>
        public IBrush RowBackground => this.IsProtected == true
            ? new SolidColorBrush(Color.FromArgb(64, 200, 0, 0))
            : Brushes.Transparent;

        /// <summary>
        /// The state as last read from Steam, used to work out what actually
        /// needs storing.
        /// </summary>
        public bool OriginalValue { get; set; }

        [ObservableProperty]
        private bool _IsAchieved;

        [ObservableProperty]
        private Bitmap _Icon;

        public string CurrentIconName => this.IsAchieved == true ? this.IconNormal : this.IconLocked;

        public string DisplayName =>
            this.Name != null && this.Name.StartsWith("#", StringComparison.InvariantCulture) == true
                ? this.Id
                : this.Name;

        public string DisplayDescription =>
            this.Name != null && this.Name.StartsWith("#", StringComparison.InvariantCulture) == true
                ? ""
                : this.Description;

        public string UnlockTimeText =>
            this.UnlockTime.HasValue == true
                ? this.UnlockTime.Value.ToString(CultureInfo.CurrentCulture)
                : "";
    }
}
