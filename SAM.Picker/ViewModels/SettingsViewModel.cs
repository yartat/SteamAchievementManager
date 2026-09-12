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
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SAM.Shared;

namespace SAM.Picker.ViewModels
{
    internal sealed partial class SettingsViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _DatabasePath;

        [ObservableProperty]
        private string _IconCachePath;

        /// <summary>Asks the view for a folder; returns null if cancelled.</summary>
        public event Func<string, string, Task<string>> FolderRequested;

        /// <summary>Raised with true when the user accepted the changes.</summary>
        public event Action<bool> CloseRequested;

        public SettingsViewModel(AppSettings settings)
        {
            this.DatabasePath = settings.DatabasePath;
            this.IconCachePath = settings.IconCachePath;
        }

        /// <summary>The database is a file, so browsing picks its directory.</summary>
        [RelayCommand]
        private async Task BrowseDatabaseAsync()
        {
            var current = Path.GetDirectoryName(this.DatabasePath);
            var picked = await this.RequestFolderAsync("Choose where to keep the game database", current);
            if (picked == null)
            {
                return;
            }
            var fileName = Path.GetFileName(this.DatabasePath);
            if (string.IsNullOrWhiteSpace(fileName) == true)
            {
                fileName = "games.db";
            }
            this.DatabasePath = Path.Combine(picked, fileName);
        }

        [RelayCommand]
        private async Task BrowseIconsAsync()
        {
            var picked = await this.RequestFolderAsync("Choose where to keep cached game icons", this.IconCachePath);
            if (picked != null)
            {
                this.IconCachePath = picked;
            }
        }

        private Task<string> RequestFolderAsync(string title, string start)
        {
            return this.FolderRequested == null
                ? Task.FromResult<string>(null)
                : this.FolderRequested(title, start);
        }

        [RelayCommand]
        private void RestoreDefaults()
        {
            this.DatabasePath = AppSettings.DefaultDatabasePath;
            this.IconCachePath = AppSettings.DefaultIconCachePath;
        }

        [RelayCommand]
        private void Save()
        {
            this.CloseRequested?.Invoke(true);
        }

        [RelayCommand]
        private void Cancel()
        {
            this.CloseRequested?.Invoke(false);
        }

        public AppSettings ToSettings()
        {
            return new()
            {
                DatabasePath = this.DatabasePath?.Trim(),
                IconCachePath = this.IconCachePath?.Trim(),
            };
        }
    }
}
