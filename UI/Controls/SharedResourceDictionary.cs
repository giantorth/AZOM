using System;
using System.Collections.Generic;
using System.Windows;

namespace MozaControls
{
    /// <summary>
    /// Merges one cached instance of the dictionary at <see cref="SharedSource"/>
    /// rather than loading a fresh copy per control, as a plain
    /// <c>ResourceDictionary Source=</c> does. Cached per thread because the
    /// contents are dispatcher-affine. The theme dictionaries it serves are
    /// self-contained and hold only shareable resources (brushes, styles,
    /// templates, geometries) that nothing mutates or animates.
    /// </summary>
    public sealed class SharedResourceDictionary : ResourceDictionary
    {
        [ThreadStatic] private static Dictionary<Uri, ResourceDictionary>? s_cache;

        private Uri? _sharedSource;

        public Uri? SharedSource
        {
            get => _sharedSource;
            set
            {
                if (value == null || _sharedSource != null) return;
                _sharedSource = value;
                var cache = s_cache ??= new Dictionary<Uri, ResourceDictionary>();
                if (!cache.TryGetValue(value, out var dict))
                {
                    dict = new ResourceDictionary { Source = value };
                    cache[value] = dict;
                }
                MergedDictionaries.Add(dict);
            }
        }
    }
}
