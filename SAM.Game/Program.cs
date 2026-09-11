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
using System.Diagnostics;
using System.IO;
using Avalonia;

namespace SAM.Game
{
    internal static class Program
    {
        internal static bool IsRunningFromSteamDirectory()
        {
            var installPath = API.Steam.GetInstallPath();
            if (string.IsNullOrEmpty(installPath) == true)
            {
                return false;
            }
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
                StringComparison.OrdinalIgnoreCase);
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                // The apphost has no extension outside Windows.
                var picker = OperatingSystem.IsWindows() == true ? "SAM.Picker.exe" : "SAM.Picker";
                Process.Start(Path.Combine(AppContext.BaseDirectory, picker));
                return;
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // Referenced by the Avalonia XAML previewer.
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
        }
    }
}
