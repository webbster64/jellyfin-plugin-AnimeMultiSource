using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class AnimeListMapper
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private static Dictionary<string, AnimeMapping> _mappingsByTvdb = new();
        private static Dictionary<string, AnimeMapping> _mappingsByImdb = new();
        private static Dictionary<long, AnimeMapping> _mappingsByAniList = new();
        private static Dictionary<long, AnimeMapping> _mappingsByMal = new();
        private static DateTime _lastUpdated = DateTime.MinValue;
        private static readonly SemaphoreSlim LoadLock = new(1, 1);
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(Constants.AnimeListCacheHours);

        public AnimeListMapper(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task LoadAnimeListsAsync()
        {
            if (DateTime.UtcNow - _lastUpdated < CacheDuration)
            {
                return;
            }

            await LoadLock.WaitAsync();
            try
            {
                if (DateTime.UtcNow - _lastUpdated < CacheDuration)
                {
                    return;
                }

                _logger.LogInformation("Loading anime lists from Fribb...");

                var options = new JsonSerializerOptions
                {
                    NumberHandling = JsonNumberHandling.AllowReadingFromString,
                    PropertyNameCaseInsensitive = true
                };

                var json = await _httpClient.GetStringAsync(Constants.FribbAnimeListsUrl);
                var mappings = JsonSerializer.Deserialize<List<AnimeMapping>>(json, options);

                // Handle duplicates by taking the first occurrence
                var mappingsByTvdb = mappings?
                    .Where(x => x.TvdbId.HasValue)
                    .GroupBy(x => x.TvdbId!.Value.ToString(CultureInfo.InvariantCulture))
                    .ToDictionary(g => g.Key, g => SelectPreferredMapping(g)) // Prefer TV over OVA/OVA specials for duplicates
                    ?? new Dictionary<string, AnimeMapping>();

                var mappingsByImdb = mappings?
                    .SelectMany(m => m.imdb_id.Select(id => (id, m)))
                    .Where(x => !string.IsNullOrEmpty(x.id))
                    .GroupBy(x => x.id)
                    .ToDictionary(g => g.Key, g => SelectPreferredMapping(g.Select(x => x.m)))
                    ?? new Dictionary<string, AnimeMapping>();

                var mappingsByAniList = mappings?
                    .Where(x => x.anilist_id.HasValue)
                    .GroupBy(x => x.anilist_id!.Value)
                    .ToDictionary(g => g.Key, g => SelectPreferredMapping(g))
                    ?? new Dictionary<long, AnimeMapping>();

                var mappingsByMal = mappings?
                    .Where(x => x.mal_id.HasValue)
                    .GroupBy(x => x.mal_id!.Value)
                    .ToDictionary(g => g.Key, g => SelectPreferredMapping(g))
                    ?? new Dictionary<long, AnimeMapping>();

                await BackfillFromOfflineDatabaseAsync(mappingsByAniList);

                _mappingsByTvdb = mappingsByTvdb;
                _mappingsByImdb = mappingsByImdb;
                _mappingsByAniList = mappingsByAniList;
                _mappingsByMal = mappingsByMal;
                _lastUpdated = DateTime.UtcNow;

                _logger.LogInformation("Loaded {Count} anime mappings with {TvdbCount} TVDB entries and {ImdbCount} IMDb entries",
                    mappings?.Count ?? 0, _mappingsByTvdb.Count, _mappingsByImdb.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load anime lists");
                throw;
            }
            finally
            {
                LoadLock.Release();
            }
        }

        // Fribb's list sometimes lacks anidb/kitsu/anisearch/livechart ids for very new titles
        // it hasn't cross-referenced yet. Backfill those specific gaps (never overwrite what
        // Fribb already has) from Fribb's own copy of manami-project/anime-offline-database,
        // which shares this exact schema.
        private async Task BackfillFromOfflineDatabaseAsync(Dictionary<long, AnimeMapping> mappingsByAniList)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    NumberHandling = JsonNumberHandling.AllowReadingFromString,
                    PropertyNameCaseInsensitive = true
                };

                var json = await _httpClient.GetStringAsync(Constants.AnimeOfflineDatabaseUrl);
                var supplemental = JsonSerializer.Deserialize<List<AnimeMapping>>(json, options);
                if (supplemental == null)
                {
                    return;
                }

                var backfilled = 0;
                foreach (var entry in supplemental)
                {
                    if (!entry.anilist_id.HasValue || !mappingsByAniList.TryGetValue(entry.anilist_id.Value, out var existing))
                    {
                        continue;
                    }

                    var changed = false;
                    if (!existing.anidb_id.HasValue && entry.anidb_id.HasValue)
                    {
                        existing.anidb_id = entry.anidb_id;
                        changed = true;
                    }

                    if (!existing.kitsu_id.HasValue && entry.kitsu_id.HasValue)
                    {
                        existing.kitsu_id = entry.kitsu_id;
                        changed = true;
                    }

                    if (!existing.anisearch_id.HasValue && entry.anisearch_id.HasValue)
                    {
                        existing.anisearch_id = entry.anisearch_id;
                        changed = true;
                    }

                    if (!existing.livechart_id.HasValue && entry.livechart_id.HasValue)
                    {
                        existing.livechart_id = entry.livechart_id;
                        changed = true;
                    }

                    if (changed)
                    {
                        backfilled++;
                    }
                }

                _logger.LogInformation("Backfilled {Count} anime mappings from anime-offline-database", backfilled);
            }
            catch (Exception ex)
            {
                // Purely supplemental; Fribb's list alone is enough to keep working.
                _logger.LogWarning(ex, "Failed to load anime-offline-database backfill; continuing with Fribb data only");
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

        public AnimeMapping? GetMappingByMalId(long malId)
        {
            return _mappingsByMal.TryGetValue(malId, out var mapping) ? mapping : null;
        }

        private AnimeMapping SelectPreferredMapping(IEnumerable<AnimeMapping> group)
        {
            return group
                .OrderBy(m => GetTypePriority(m.type))
                .ThenBy(m => m.anilist_id ?? long.MaxValue) // deterministic fallback
                .First();
        }

        private int GetTypePriority(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return 1; // slightly below explicit TV
            }

            var normalized = type.Trim().ToUpperInvariant();

            // Prefer TV series mappings over OVA specials when IDs collide.
            return normalized switch
            {
                "TV" => 0,
                "TV SERIES" => 0,
                "ONA" => 2,
                "SPECIAL" => 3,
                "MOVIE" => 4,
                "OVA" => 5,
                _ => 6
            };
        }
    }

    public class AnimeMapping
    {
        // Support both legacy "thetvdb_id" and current "tvdb_id" keys.
        [JsonPropertyName("tvdb_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? tvdb_id { get; set; }

        [JsonPropertyName("thetvdb_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? thetvdb_id { get; set; }

        [JsonIgnore]
        public long? TvdbId => tvdb_id ?? thetvdb_id;

        public string[] imdb_id { get; set; } = [];

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? anisearch_id { get; set; }

        [JsonPropertyName("themoviedb_id")]
        public JsonElement? themoviedb_id { get; set; }

        [JsonIgnore]
        public long? PreferredTmdbId
        {
            get
            {
                if (themoviedb_id == null)
                    return null;

                var value = themoviedb_id.Value;

                if (value.ValueKind == JsonValueKind.Number)
                    return value.GetInt64();

                if (value.ValueKind == JsonValueKind.String &&
                    long.TryParse(value.GetString(), out var id))
                    return id;

                if (value.ValueKind == JsonValueKind.Object)
                {
                    if (value.TryGetProperty("tv", out var tv))
                        return tv.GetInt64();

                    if (value.TryGetProperty("movie", out var movie))
                        return movie.GetInt64();
                }

                return null;
            }
        }

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
