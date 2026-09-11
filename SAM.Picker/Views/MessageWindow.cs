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

using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SAM.Picker.Views
{
    /// <summary>
    /// Minimal stand-in for the WinForms MessageBox, which Avalonia has no
    /// equivalent of. Built in code rather than XAML because it is one button
    /// and one label.
    /// </summary>
    internal sealed class MessageWindow : Window
    {
        private MessageWindow(string title, string message)
        {
            this.Title = title;
            this.SizeToContent = SizeToContent.WidthAndHeight;
            this.CanResize = false;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.ShowInTaskbar = true;

            Button ok = new()
            {
                Content = "OK",
                MinWidth = 88,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true,
            };
            ok.Click += (_, _) => this.Close();

            this.Content = new StackPanel()
            {
                Margin = new(20),
                Spacing = 16,
                MaxWidth = 520,
                Children =
                {
                    new TextBlock()
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    ok,
                },
            };
        }

        public static MessageWindow CreateError(string message)
        {
            return new("Error", message);
        }

        public static Task ShowErrorAsync(Window owner, string message)
        {
            MessageWindow window = new("Error", message);
            return owner == null
                ? Task.CompletedTask
                : window.ShowDialog(owner);
        }
    }
}
