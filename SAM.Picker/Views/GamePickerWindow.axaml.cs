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

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SAM.Picker.ViewModels;

namespace SAM.Picker.Views
{
    public partial class GamePickerWindow : Window
    {
        private GamePickerViewModel _ViewModel;

        public GamePickerWindow()
        {
            this.InitializeComponent();
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            if (this._ViewModel != null)
            {
                this._ViewModel.ErrorRaised -= this.OnErrorRaised;
            }

            this._ViewModel = this.DataContext as GamePickerViewModel;

            if (this._ViewModel != null)
            {
                this._ViewModel.ErrorRaised += this.OnErrorRaised;
                this._ViewModel.SettingsRequested += this.OnSettingsRequested;
            }

            base.OnDataContextChanged(e);
        }

        private async System.Threading.Tasks.Task<AppSettings> OnSettingsRequested(AppSettings current)
        {
            SettingsViewModel viewModel = new(current);
            SettingsWindow window = new() { DataContext = viewModel };
            var accepted = await window.ShowDialog<bool>(this);
            return accepted == true ? viewModel.ToSettings() : null;
        }

        protected override void OnClosed(System.EventArgs e)
        {
            if (this._ViewModel != null)
            {
                this._ViewModel.ErrorRaised -= this.OnErrorRaised;
                this._ViewModel.SettingsRequested -= this.OnSettingsRequested;
                this._ViewModel.Shutdown();
            }
            base.OnClosed(e);
        }

        private void OnErrorRaised(string message)
        {
            _ = MessageWindow.ShowErrorAsync(this, message);
        }

        private void OnGameDoubleTapped(object sender, TappedEventArgs e)
        {
            this._ViewModel?.LaunchSelectedCommand.Execute(null);
        }

        private void OnGameListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            this._ViewModel?.LaunchSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }
}
