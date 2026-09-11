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
using System.Threading.Tasks;
using Avalonia.Controls;
using SAM.Game.ViewModels;

namespace SAM.Game.Views
{
    public partial class ManagerWindow : Window
    {
        private ManagerViewModel _ViewModel;

        public ManagerWindow()
        {
            this.InitializeComponent();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            this.Detach();

            this._ViewModel = this.DataContext as ManagerViewModel;

            if (this._ViewModel != null)
            {
                this._ViewModel.MessageRaised += this.OnMessageRaised;
                this._ViewModel.ConfirmRequested += this.OnConfirmRequested;
            }

            base.OnDataContextChanged(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            this._ViewModel?.Shutdown();
            this.Detach();
            base.OnClosed(e);
        }

        private void Detach()
        {
            if (this._ViewModel == null)
            {
                return;
            }
            this._ViewModel.MessageRaised -= this.OnMessageRaised;
            this._ViewModel.ConfirmRequested -= this.OnConfirmRequested;
        }

        private void OnMessageRaised(string message, bool isInformation)
        {
            _ = isInformation == true
                ? MessageWindow.ShowInfoAsync(this, message)
                : MessageWindow.ShowErrorAsync(this, message);
        }

        private Task<bool> OnConfirmRequested(string title, string message)
        {
            return MessageWindow.ShowConfirmAsync(this, title, message);
        }
    }
}
