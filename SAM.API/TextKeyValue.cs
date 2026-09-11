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
using System.IO;
using System.Text;

namespace SAM.API
{
    /// <summary>
    /// Minimal reader for Valve's <em>text</em> KeyValues format, as used by
    /// <c>userdata/&lt;id&gt;/config/localconfig.vdf</c>.
    /// <see cref="KeyValue"/> only handles the binary flavour.
    /// </summary>
    /// <remarks>
    /// Values routinely contain embedded JSON with escaped quotes and braces,
    /// so this has to be a real parser — scanning for a key by regular
    /// expression will match inside string values and inside the unrelated
    /// second <c>apps</c> section further down the file.
    /// </remarks>
    public sealed class TextKeyValue
    {
        private readonly Dictionary<string, TextKeyValue> _Children;

        public string Value { get; }

        private TextKeyValue(string value, Dictionary<string, TextKeyValue> children)
        {
            this.Value = value;
            this._Children = children;
        }

        public static readonly TextKeyValue Invalid = new(null, null);

        public bool Valid => this != Invalid;

        /// <summary>Child lookup; case-insensitive, as Valve's own reader is.</summary>
        public TextKeyValue this[string key]
        {
            get
            {
                if (this._Children == null ||
                    this._Children.TryGetValue(key, out var child) == false)
                {
                    return Invalid;
                }
                return child;
            }
        }

        public IEnumerable<KeyValuePair<string, TextKeyValue>> Children =>
            this._Children ?? (IEnumerable<KeyValuePair<string, TextKeyValue>>)Array.Empty<KeyValuePair<string, TextKeyValue>>();

        public int AsInteger(int defaultValue)
        {
            return this.Value != null &&
                   int.TryParse(this.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : defaultValue;
        }

        public static TextKeyValue LoadFromFile(string path)
        {
            try
            {
                return Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception)
            {
                return Invalid;
            }
        }

        public static TextKeyValue Parse(string text)
        {
            int position = 0;
            var root = ParseObject(text, ref position);
            return root ?? Invalid;
        }

        private static TextKeyValue ParseObject(string text, ref int position)
        {
            Dictionary<string, TextKeyValue> children = new(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                SkipWhitespaceAndComments(text, ref position);
                if (position >= text.Length || text[position] == '}')
                {
                    position++;
                    break;
                }

                var key = ReadToken(text, ref position);
                if (key == null)
                {
                    break;
                }

                SkipWhitespaceAndComments(text, ref position);
                if (position >= text.Length)
                {
                    break;
                }

                if (text[position] == '{')
                {
                    position++;
                    children[key] = ParseObject(text, ref position);
                }
                else
                {
                    var value = ReadToken(text, ref position);
                    children[key] = new(value ?? "", null);
                }
            }

            return new(null, children);
        }

        private static void SkipWhitespaceAndComments(string text, ref int position)
        {
            while (position < text.Length)
            {
                var c = text[position];
                if (char.IsWhiteSpace(c) == true)
                {
                    position++;
                }
                else if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
                {
                    while (position < text.Length && text[position] != '\n')
                    {
                        position++;
                    }
                }
                else
                {
                    break;
                }
            }
        }

        private static string ReadToken(string text, ref int position)
        {
            SkipWhitespaceAndComments(text, ref position);
            if (position >= text.Length)
            {
                return null;
            }

            if (text[position] != '"')
            {
                // Unquoted token; runs to the next whitespace or brace.
                var start = position;
                while (position < text.Length &&
                       char.IsWhiteSpace(text[position]) == false &&
                       text[position] != '{' &&
                       text[position] != '}')
                {
                    position++;
                }
                return position > start ? text[start..position] : null;
            }

            position++;
            StringBuilder builder = new();
            while (position < text.Length && text[position] != '"')
            {
                if (text[position] == '\\' && position + 1 < text.Length)
                {
                    position++;
                    builder.Append(text[position] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        var other => other,
                    });
                }
                else
                {
                    builder.Append(text[position]);
                }
                position++;
            }
            position++;
            return builder.ToString();
        }
    }
}
