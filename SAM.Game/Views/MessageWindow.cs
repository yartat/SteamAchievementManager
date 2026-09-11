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

namespace SAM.Game.Views
{
    /// <summary>
    /// Minimal stand-in for the WinForms MessageBox, which Avalonia has no
    /// equivalent of.
    /// </summary>
    internal sealed class MessageWindow : Window
    {
        private MessageWindow(string title, string message, bool confirm)
        {
            this.Title = title;
            this.SizeToContent = SizeToContent.WidthAndHeight;
            this.CanResize = false;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            StackPanel buttons = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            if (confirm == true)
            {
                Button yes = new() { Content = "Yes", MinWidth = 88, IsDefault = true };
                Button no = new() { Content = "No", MinWidth = 88, IsCancel = true };
                yes.Click += (_, _) => this.Close(true);
                no.Click += (_, _) => this.Close(false);
                buttons.Children.Add(yes);
                buttons.Children.Add(no);
            }
            else
            {
                Button ok = new() { Content = "OK", MinWidth = 88, IsDefault = true, IsCancel = true };
                ok.Click += (_, _) => this.Close(false);
                buttons.Children.Add(ok);
            }

            this.Content = new StackPanel()
            {
                Margin = new(20),
                Spacing = 16,
                MaxWidth = 520,
                Children =
                {
                    new TextBlock() { Text = message, TextWrapping = TextWrapping.Wrap },
                    buttons,
                },
            };
        }

        public static MessageWindow CreateError(string message)
        {
            return new("Error", message, false);
        }

        public static Task ShowErrorAsync(Window owner, string message)
        {
            return new MessageWindow("Error", message, false).ShowDialog(owner);
        }

        public static Task ShowInfoAsync(Window owner, string message)
        {
            return new MessageWindow("Information", message, false).ShowDialog(owner);
        }

        public static Task<bool> ShowConfirmAsync(Window owner, string title, string message)
        {
            return new MessageWindow(title, message, true).ShowDialog<bool>(owner);
        }
    }
}
