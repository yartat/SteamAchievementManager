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

using System.Globalization;

namespace SAM.Game.Stats
{
    internal sealed class IntStatInfo : StatInfo
    {
        public int OriginalValue;
        public int IntValue;

        public override string ValueText
        {
            get => this.IntValue.ToString(CultureInfo.CurrentCulture);
            set
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var i) == false)
                {
                    this.SetValueError("Invalid value");
                    return;
                }

                if (this.IsProtected == true && this.IntValue != i)
                {
                    this.SetValueError("Stat is protected! -- you can't modify it");
                    return;
                }

                this.SetValueError(null);
                if (this.IntValue == i)
                {
                    return;
                }
                this.IntValue = i;
                this.OnPropertyChanged(nameof(this.ValueText));
                this.OnPropertyChanged(nameof(this.IsModified));
            }
        }

        public override bool IsModified => this.IntValue != this.OriginalValue;
    }
}
