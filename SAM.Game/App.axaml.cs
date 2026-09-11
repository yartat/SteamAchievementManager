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
using SAM.Game.ViewModels;
using SAM.Game.Views;

namespace SAM.Game
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
                desktop.MainWindow = this.CreateMainWindow(desktop.Args);
                desktop.ShutdownRequested += this.OnShutdownRequested;
            }
            base.OnFrameworkInitializationCompleted();
        }

        private Avalonia.Controls.Window CreateMainWindow(string[] args)
        {
            if (args == null || args.Length == 0 ||
                long.TryParse(args[0], out var appId) == false)
            {
                return MessageWindow.CreateError(
                    "Could not parse application ID from command line argument.");
            }

            if (Program.IsRunningFromSteamDirectory() == true)
            {
                return MessageWindow.CreateError("This tool declines to being run from the Steam directory.");
            }

            API.Client client = new();
            try
            {
                client.Initialize(appId);
            }
            catch (API.ClientInitializeException e)
            {
                client.Dispose();
                string message;
                if (e.Failure == API.ClientInitializeFailure.ConnectToGlobalUser)
                {
                    message =
                        "Steam is not running. Please start Steam then run this tool again.\n\n" +
                        "If you have the game through Family Share, the game may be locked due to\n" +
                        "the Family Share account actively playing a game.\n\n" +
                        "(" + e.Message + ")";
                }
                else if (string.IsNullOrEmpty(e.Message) == false)
                {
                    message =
                        "Steam is not running. Please start Steam then run this tool again.\n\n" +
                        "(" + e.Message + ")";
                }
                else
                {
                    message = "Steam is not running. Please start Steam then run this tool again.";
                }
                return MessageWindow.CreateError(message);
            }
            catch (DllNotFoundException)
            {
                client.Dispose();
                return MessageWindow.CreateError("You've caused an exceptional error!");
            }

            this._SteamClient = client;
            return new ManagerWindow()
            {
                DataContext = new ManagerViewModel(appId, client),
            };
        }

        private void OnShutdownRequested(object sender, ShutdownRequestedEventArgs e)
        {
            this._SteamClient?.Dispose();
            this._SteamClient = null;
        }
    }
}
