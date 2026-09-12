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
using CommunityToolkit.Mvvm.ComponentModel;

namespace SAM.Shared
{
    /// <summary>
    /// The status bar's progress indicator. Both windows derive from this so a
    /// long job reports itself the same way in each.
    /// </summary>
    /// <remarks>
    /// This replaced a pair of download-only members. Several of the phases now
    /// reported download nothing — the ownership sweep is Steam calls, the
    /// library stats pass is file reads — so the indicator is named for what it
    /// shows rather than for the one job it started out covering.
    /// <para>
    /// Every member here is set from the UI thread. Work running in
    /// <c>Task.Run</c> reports through an <see cref="IProgress{T}"/> created on
    /// the UI thread, which marshals the callback back for us.
    /// </para>
    /// </remarks>
    internal abstract partial class ProgressViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _ProgressText = "";

        [ObservableProperty]
        private bool _IsProgressVisible;

        [ObservableProperty]
        private bool _IsProgressIndeterminate;

        [ObservableProperty]
        private double _ProgressValue;

        [ObservableProperty]
        private double _ProgressMaximum = 1;

        /// <summary>
        /// How many jobs are running. The picker's startup decodes cached
        /// icons, refreshes the library and drains the logo queue at the same
        /// time, all reporting into this one indicator; without a count the
        /// first of them to finish would take the bar down while the others
        /// were still working.
        /// </summary>
        private int _ActiveJobs;

        /// <summary>
        /// Starts a job, showing a running bar for work whose size is not known
        /// yet: a download that has not reported a length, or a Steam call
        /// answered by callback. Every call must be paired with
        /// <see cref="EndProgress"/>, including on a failure path.
        /// </summary>
        protected void BeginProgress(string text)
        {
            this._ActiveJobs++;
            this.ProgressText = text;
            this.ProgressValue = 0;
            this.IsProgressIndeterminate = true;
            this.IsProgressVisible = true;
        }

        /// <summary>
        /// Counts <paramref name="completed"/> out of <paramref name="total"/>.
        /// A total that is not yet known falls back to the running bar rather
        /// than dividing by zero or showing a full one.
        /// </summary>
        protected void ReportProgress(string text, int completed, int total)
        {
            if (total <= 0)
            {
                this.BeginProgress(text);
                return;
            }

            this.ProgressText = text;
            this.ProgressMaximum = total;
            this.ProgressValue = Math.Clamp(completed, 0, total);
            this.IsProgressIndeterminate = false;
            this.IsProgressVisible = true;
        }

        /// <summary>
        /// Ends one job. The bar comes down when the last one finishes, not the
        /// first.
        /// </summary>
        protected void EndProgress()
        {
            this._ActiveJobs = Math.Max(0, this._ActiveJobs - 1);
            if (this._ActiveJobs > 0)
            {
                return;
            }

            this.IsProgressVisible = false;
            this.IsProgressIndeterminate = false;
            this.ProgressValue = 0;
        }

        /// <summary>
        /// How often a loop of <paramref name="total"/> items should report, so
        /// a sweep over tens of thousands of app ids does not post a message
        /// per item to the UI thread. Roughly 200 updates whatever the size.
        /// </summary>
        protected static int ProgressStep(int total)
        {
            return Math.Max(1, total / 200);
        }
    }
}
