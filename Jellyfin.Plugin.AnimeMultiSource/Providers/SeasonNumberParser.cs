using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Jellyfin's own folder-name parsing only recognizes English season folders
    // ("Season 1", "S1"), so libraries using a localized season folder name (e.g. French
    // "Saison 1") get no IndexNumber at all and this plugin has nothing to work with. This
    // is a best-effort text fallback covering season folder names in several common languages,
    // used only when Jellyfin didn't already resolve a season number by itself.
    public static class SeasonNumberParser
    {
        private static readonly Regex NumberedPattern = new(
            @"(?:season|saison|staffel|temporada|stagione|sezon)\s*0*(\d+)|(?:^|\b)s\s*0*(\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static int? TryParse(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var trimmed = name.Trim();
            if (trimmed.Equals("Specials", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var match = NumberedPattern.Match(trimmed);
            if (!match.Success)
            {
                return null;
            }

            var group = match.Groups[1].Success ? match.Groups[1] : match.Groups[2];
            return int.TryParse(group.Value, out var number) ? number : null;
        }
    }
}
