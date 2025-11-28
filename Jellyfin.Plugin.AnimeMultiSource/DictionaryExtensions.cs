using System.Collections.Generic;

namespace Jellyfin.Plugin.AnimeMultiSource
{
    public static class DictionaryExtensions
    {
        public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue? defaultValue = default) where TKey : notnull
        {
            if (dictionary == null) return defaultValue;
            return dictionary.TryGetValue(key, out TValue? value) ? value : defaultValue;
        }
    }
}
