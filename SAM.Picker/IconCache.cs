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
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SAM.Picker
{
    /// <summary>
    /// On-disk cache of downloaded capsule images, one file per app id.
    /// </summary>
    /// <remarks>
    /// The file name carries the app id only; the URL it was fetched from is
    /// kept in the database alongside the game. When Steam changes a capsule
    /// the stored URL stops matching and the image is re-fetched, so the two
    /// stores stay honest about each other without a second index here.
    /// </remarks>
    internal sealed class IconCache
    {
        public string Path { get; private set; }

        public IconCache(string path)
        {
            this.Path = path;
            TryCreate(path);
        }

        private static void TryCreate(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception)
            {
                // Surfaced when a read or write actually fails.
            }
        }

        private string FileFor(uint appId)
        {
            return System.IO.Path.Combine(
                this.Path,
                appId.ToString(CultureInfo.InvariantCulture) + ".img");
        }

        public byte[] TryRead(uint appId)
        {
            try
            {
                var file = this.FileFor(appId);
                return File.Exists(file) == true ? File.ReadAllBytes(file) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Write(uint appId, byte[] data)
        {
            try
            {
                TryCreate(this.Path);
                File.WriteAllBytes(this.FileFor(appId), data);
            }
            catch (Exception)
            {
                // A cache that cannot be written is a slow app, not a broken one.
            }
        }

        /// <summary>Drops cached images for games that are no longer owned.</summary>
        public void Prune(ICollection<uint> keep)
        {
            try
            {
                if (Directory.Exists(this.Path) == false)
                {
                    return;
                }
                foreach (var file in Directory.GetFiles(this.Path, "*.img"))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    if (uint.TryParse(name, out var appId) == true && keep.Contains(appId) == true)
                    {
                        continue;
                    }
                    File.Delete(file);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Moves every cached image to a new directory.</summary>
        public void MoveTo(string destination)
        {
            var source = this.Path;
            if (string.Equals(
                    System.IO.Path.GetFullPath(source),
                    System.IO.Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                return;
            }

            TryCreate(destination);

            if (Directory.Exists(source) == true)
            {
                foreach (var file in Directory.GetFiles(source, "*.img"))
                {
                    try
                    {
                        File.Move(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)), true);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            this.Path = destination;
        }
    }
}
