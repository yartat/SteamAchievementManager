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
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SAM.Game.Stats
{
    internal abstract class StatInfo : ObservableObject, INotifyDataErrorInfo
    {
        public abstract bool IsModified { get; }
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public bool IsIncrementOnly { get; set; }
        public int Permission { get; set; }

        public bool IsProtected => (this.Permission & 2) != 0;

        /// <summary>
        /// Bound by the grid. Parsing and the protected-stat check live here
        /// because Avalonia's DataGrid surfaces failures through
        /// <see cref="INotifyDataErrorInfo"/> rather than through a
        /// DataError event the way the WinForms DataGridView did.
        /// </summary>
        public abstract string ValueText { get; set; }

        public string Extra
        {
            get
            {
                var flags = StatFlags.None;
                flags |= this.IsIncrementOnly == false ? 0 : StatFlags.IncrementOnly;
                flags |= ((this.Permission & 2) != 0) == false ? 0 : StatFlags.Protected;
                flags |= ((this.Permission & ~2) != 0) == false ? 0 : StatFlags.UnknownPermission;
                return flags.ToString();
            }
        }

        #region INotifyDataErrorInfo

        private string _ValueError;

        public bool HasErrors => this._ValueError != null;

        public event EventHandler<DataErrorsChangedEventArgs> ErrorsChanged;

        public IEnumerable GetErrors(string propertyName)
        {
            if (propertyName == nameof(this.ValueText) && this._ValueError != null)
            {
                return new[] { this._ValueError };
            }
            return Array.Empty<string>();
        }

        protected void SetValueError(string error)
        {
            if (this._ValueError == error)
            {
                return;
            }
            this._ValueError = error;
            this.ErrorsChanged?.Invoke(this, new(nameof(this.ValueText)));
            this.OnPropertyChanged(nameof(this.HasErrors));
        }

        #endregion
    }
}
