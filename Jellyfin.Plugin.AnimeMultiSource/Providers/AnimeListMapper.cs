using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class AnimeListMapper
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private Dictionary<string, AnimeMapping> _mappingsByTvdb;
        private Dictionary<string, AnimeMapping> _mappingsByImdb;
        private Dictionary<long, AnimeMapping> _mappingsByAniList;
        private DateTime _lastUpdated;

        public AnimeListMapper(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _mappingsByTvdb = new Dictionary<string, AnimeMapping>();
            _mappingsByImdb = new Dictionary<string, AnimeMapping>();
            _mappingsByAniList = new Dictionary<long, AnimeMapping>();
        }

        public async Task LoadAnimeListsAsync()
        {
            try
            {
                _logger.LogInformation("Loading anime lists from Fribb...");

                var json = await _httpClient.GetStringAsync(Constants.FribbAnimeListsUrl);

                var options = new JsonSerializerOptions
                {
                    NumberHandling = JsonNumberHandling.AllowReadingFromString,
                    PropertyNameCaseInsensitive = true
                };

                var mappings = JsonSerializer.Deserialize<List<AnimeMapping>>(json, options);

                // Handle duplicates by taking the first occurrence
                _mappingsByTvdb = mappings?
                    .Where(x => x.thetvdb_id.HasValue)
                    .GroupBy(x => x.thetvdb_id.GetValueOrDefault().ToString())
                    .ToDictionary(g => g.Key, g => g.First()) // Take first occurrence for duplicates
                    ?? new Dictionary<string, AnimeMapping>();

                _mappingsByImdb = mappings?
                    .Where(x => !string.IsNullOrEmpty(x.imdb_id))
                    .GroupBy(x => x.imdb_id!)
                    .ToDictionary(g => g.Key, g => g.First()) // Take first occurrence for duplicates
                    ?? new Dictionary<string, AnimeMapping>();

                _mappingsByAniList = mappings?
                    .Where(x => x.anilist_id.HasValue)
                    .GroupBy(x => x.anilist_id!.Value)
                    .ToDictionary(g => g.Key, g => g.First())
                    ?? new Dictionary<long, AnimeMapping>();

                _lastUpdated = DateTime.UtcNow;
                _logger.LogInformation("Loaded {Count} anime mappings with {TvdbCount} TVDB entries and {ImdbCount} IMDb entries",
                    mappings?.Count ?? 0, _mappingsByTvdb.Count, _mappingsByImdb.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load anime lists");
                throw;
            }
        }

        public AnimeMapping? GetMappingByTvdbId(string tvdbId)
        {
            return _mappingsByTvdb.TryGetValue(tvdbId, out var mapping) ? mapping : null;
        }

        public AnimeMapping? GetMappingByImdbId(string imdbId)
        {
            return _mappingsByImdb.TryGetValue(imdbId, out var mapping) ? mapping : null;
        }

        public AnimeMapping? GetMappingByAniListId(long aniListId)
        {
            return _mappingsByAniList.TryGetValue(aniListId, out var mapping) ? mapping : null;
        }
    }

    public class AnimeMapping
    {
        // Change ALL numeric ID properties to long?
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? thetvdb_id { get; set; }

        public string? imdb_id { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? anisearch_id { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? themoviedb_id { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? kitsu_id { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? mal_id { get; set; }

        public string? type { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? anilist_id { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? anidb_id { get; set; }

        public string? animeplanet_id { get; set; }

        public string? notify_moe_id { get; set; }

        // Change this to numeric type too!
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? livechart_id { get; set; }
    }
}
