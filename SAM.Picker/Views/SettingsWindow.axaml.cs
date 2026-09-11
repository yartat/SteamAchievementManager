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
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SAM.Picker.ViewModels;

namespace SAM.Picker.Views
{
    public partial class SettingsWindow : Window
    {
        private SettingsViewModel _ViewModel;

        public SettingsWindow()
        {
            this.InitializeComponent();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            this.Detach();

            this._ViewModel = this.DataContext as SettingsViewModel;
            if (this._ViewModel != null)
            {
                this._ViewModel.FolderRequested += this.OnFolderRequested;
                this._ViewModel.CloseRequested += this.OnCloseRequested;
            }

            base.OnDataContextChanged(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            this.Detach();
            base.OnClosed(e);
        }

        private void Detach()
        {
            if (this._ViewModel == null)
            {
                return;
            }
            this._ViewModel.FolderRequested -= this.OnFolderRequested;
            this._ViewModel.CloseRequested -= this.OnCloseRequested;
        }

        private async Task<string> OnFolderRequested(string title, string startIn)
        {
            IStorageFolder start = null;
            try
            {
                if (string.IsNullOrWhiteSpace(startIn) == false && Directory.Exists(startIn) == true)
                {
                    start = await this.StorageProvider.TryGetFolderFromPathAsync(startIn);
                }
            }
            catch (Exception)
            {
            }

            var picked = await this.StorageProvider.OpenFolderPickerAsync(new()
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

            return picked.Count == 0 ? null : picked[0].Path.LocalPath;
        }

        private void OnCloseRequested(bool accepted)
        {
            this.Close(accepted);
        }
    }
}
