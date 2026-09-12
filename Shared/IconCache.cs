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

namespace SAM.Shared
{
    /// <summary>
    /// On-disk cache of downloaded images, shared by both applications: the
    /// picker's capsule art, one file per app id, and the game window's
    /// achievement icons, one file per app id and icon name.
    /// </summary>
    /// <remarks>
    /// The file name carries the app id only; the URL it was fetched from is
    /// kept in the database alongside the game. When Steam changes a capsule
    /// the stored URL stops matching and the image is re-fetched, so the two
    /// stores stay honest about each other without a second index here.
    /// <para>
    /// Achievement icons are named <c>&lt;appid&gt;_&lt;icon&gt;</c> so that
    /// <see cref="Prune"/> and <see cref="MoveTo"/> can still recover the app
    /// id they belong to, and so a capsule can never collide with one.
    /// </para>
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

        /// <summary>
        /// Returns null when <paramref name="iconName"/> is anything other than
        /// a plain file name. It comes out of Steam's schema blob, so it is not
        /// allowed to steer a write out of the cache directory.
        /// </summary>
        private string FileFor(uint appId, string iconName)
        {
            if (string.IsNullOrEmpty(iconName) == true ||
                iconName != System.IO.Path.GetFileName(iconName) ||
                iconName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }
            return System.IO.Path.Combine(
                this.Path,
                appId.ToString(CultureInfo.InvariantCulture) + "_" + iconName);
        }

        public byte[] TryRead(uint appId)
        {
            return TryReadFile(this.FileFor(appId));
        }

        public byte[] TryRead(uint appId, string iconName)
        {
            return TryReadFile(this.FileFor(appId, iconName));
        }

        private static byte[] TryReadFile(string file)
        {
            if (file == null)
            {
                return null;
            }
            try
            {
                return File.Exists(file) == true ? File.ReadAllBytes(file) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Write(uint appId, byte[] data)
        {
            this.WriteFile(this.FileFor(appId), data);
        }

        public void Write(uint appId, string iconName, byte[] data)
        {
            this.WriteFile(this.FileFor(appId, iconName), data);
        }

        private void WriteFile(string file, byte[] data)
        {
            if (file == null)
            {
                return;
            }
            try
            {
                TryCreate(this.Path);
                File.WriteAllBytes(file, data);
            }
            catch (Exception)
            {
                // A cache that cannot be written is a slow app, not a broken one.
            }
        }

        /// <summary>
        /// Recovers the app id a cached file belongs to: the whole stem for a
        /// capsule, the part before the first underscore for an achievement
        /// icon. False for anything this cache did not write.
        /// </summary>
        private static bool TryGetAppId(string file, out uint appId)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            var separator = name.IndexOf('_');
            if (separator > 0)
            {
                name = name[..separator];
            }
            return uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out appId);
        }

        /// <summary>
        /// Every file in <paramref name="directory"/> that this cache wrote,
        /// paired with the app id it belongs to. Files it did not write are
        /// skipped rather than touched — the cache directory is user-chosen and
        /// may not be ours alone.
        /// </summary>
        private static IEnumerable<(string File, uint AppId)> EnumerateCacheFiles(string directory)
        {
            if (Directory.Exists(directory) == false)
            {
                yield break;
            }
            foreach (var file in Directory.GetFiles(directory))
            {
                if (TryGetAppId(file, out var appId) == false)
                {
                    continue;
                }
                yield return (file, appId);
            }
        }

        /// <summary>Drops cached images for games that are no longer owned.</summary>
        public void Prune(ICollection<uint> keep)
        {
            try
            {
                foreach (var (file, appId) in EnumerateCacheFiles(this.Path))
                {
                    if (keep.Contains(appId) == true)
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

            foreach (var (file, _) in EnumerateCacheFiles(source))
            {
                try
                {
                    File.Move(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)), true);
                }
                catch (Exception)
                {
                }
            }

            this.Path = destination;
        }
    }
}
