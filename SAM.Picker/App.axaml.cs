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
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SAM.Picker.ViewModels;
using SAM.Picker.Views;

namespace SAM.Picker
{
    public partial class App : Application
    {
        private API.Client _SteamClient;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = this.CreateMainWindow();
                desktop.ShutdownRequested += this.OnShutdownRequested;
            }
            base.OnFrameworkInitializationCompleted();
        }

        private Avalonia.Controls.Window CreateMainWindow()
        {
            if (API.SteamPlatform.IsArchitectureSupported == false)
            {
                return MessageWindow.CreateError(API.SteamPlatform.DescribeUnsupportedArchitecture());
            }

            if (Program.IsRunningFromSteamDirectory() == true)
            {
                return MessageWindow.CreateError("This tool declines to being run from the Steam directory.");
            }

            API.Client client = new();
            try
            {
                client.Initialize(0);
            }
            catch (API.ClientInitializeException e)
            {
                client.Dispose();
                var detail = string.IsNullOrEmpty(e.Message) == false
                    ? "\n\n(" + e.Message + ")"
                    : "";
                return MessageWindow.CreateError(
                    "Steam is not running. Please start Steam then run this tool again." + detail);
            }
            catch (DllNotFoundException)
            {
                client.Dispose();
                return MessageWindow.CreateError("You've caused an exceptional error!");
            }

            this._SteamClient = client;
            return new GamePickerWindow()
            {
                DataContext = new GamePickerViewModel(client),
            };
        }

        private void OnShutdownRequested(object sender, ShutdownRequestedEventArgs e)
        {
            this._SteamClient?.Dispose();
            this._SteamClient = null;
        }
    }
}
