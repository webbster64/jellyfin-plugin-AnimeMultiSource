using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class PlexMatchParser
    {
        private readonly ILogger _logger;

        public PlexMatchParser(ILogger logger)
        {
            _logger = logger;
        }

        public PlexMatchData ParsePlexMatch(string content)
        {
            var data = new PlexMatchData();

            foreach (var line in content.Split('\n'))
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine)) continue;

                var parts = trimmedLine.Split(new[] { ':' }, 2);
                if (parts.Length != 2) continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim();

                switch (key.ToLowerInvariant())
                {
                    case "title":
                        data.Title = value;
                        _logger?.LogDebug("Parsed Title: {Title}", value);
                        break;
                    case "year":
                        if (int.TryParse(value, out int year))
                        {
                            data.Year = year;
                            _logger?.LogDebug("Parsed Year: {Year}", year);
                        }
                        break;
                    case "tvdbid":
                        data.TvdbId = value;
                        _logger?.LogDebug("Parsed TVDB ID: {TvdbId}", value);
                        break;
                    case "imdbid":
                        data.ImdbId = value;
                        _logger?.LogDebug("Parsed IMDb ID: {ImdbId}", value);
                        break;
                }
            }

            return data;
        }
    }

    public class PlexMatchData
    {
        public string Title { get; set; } = string.Empty;
        public int? Year { get; set; }
        public string TvdbId { get; set; } = string.Empty;
        public string ImdbId { get; set; } = string.Empty;
    }
}