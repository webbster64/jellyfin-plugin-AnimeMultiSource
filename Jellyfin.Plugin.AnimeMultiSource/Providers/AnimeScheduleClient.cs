using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Thin client for the AnimeSchedule.net v3 API (https://animeschedule.net/api/v3/documentation).
    // Every request requires a Bearer token the user generates themselves in their AnimeSchedule
    // account settings (API tab); this client is a no-op wherever that key hasn't been configured.
    public class AnimeScheduleClient
    {
        private const string BaseUrl = "https://animeschedule.net/api/v3";

        private static readonly TimeSpan AnimeDetailCacheDuration = TimeSpan.FromDays(7);
        private static readonly TimeSpan TimetableCacheDuration = TimeSpan.FromHours(6);
        private static readonly ConcurrentDictionary<long, CacheEntry<AnimeDetail?>> AnimeDetailCache = new();
        private static readonly ConcurrentDictionary<long, CacheEntry<AnimeDetail?>> AnimeDetailByAniDbCache = new();
        private static readonly ConcurrentDictionary<string, CacheEntry<List<TimetableEntry>>> TimetableCache = new();
        private static readonly object PersistentCacheLock = new();
        private static bool _persistentCacheLoaded;
        private static string? _persistentCachePath;

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly string _apiKey;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public AnimeScheduleClient(HttpClient httpClient, ILogger logger, string apiKey)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiKey = apiKey ?? string.Empty;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        // Resolves an AniList id to the AnimeSchedule "route" (URL slug) used to match timetable
        // entries, via GET /anime?anilist-ids={id} — a direct id lookup, no fuzzy title matching.
        public async Task<string?> GetRouteByAniListIdAsync(long aniListId)
        {
            var detail = await GetAnimeDetailByAniListIdAsync(aniListId);
            return detail?.Route;
        }

        // Same GET /anime?anilist-ids={id} lookup, but exposes the full anime record - AnimeSchedule
        // curates its own title translations and (often Crunchyroll-sourced) synopsis, which is a
        // useful third, independently-sourced fallback for title/overview resolution when both
        // AniList and Jikan/MAL come up empty (e.g. an outage on one or both of them).
        public Task<AnimeDetail?> GetAnimeDetailByAniListIdAsync(long aniListId)
        {
            return FetchAnimeDetailAsync($"anilist-ids={aniListId}", AnimeDetailCache, aniListId, "AniList", aniListId);
        }

        // GET /anime?anidb-ids={id} - AnimeSchedule maintains its own AniDB/AniList/MAL cross-links
        // (via the "websites" field on the result), independent of Fribb/anime-lists. Useful as an
        // ID backfill when a Fribb mapping has an anidb_id but is missing anilist_id/mal_id for a
        // title it hasn't fully cross-referenced yet.
        public Task<AnimeDetail?> GetAnimeDetailByAniDbIdAsync(long aniDbId)
        {
            return FetchAnimeDetailAsync($"anidb-ids={aniDbId}", AnimeDetailByAniDbCache, aniDbId, "AniDB", aniDbId);
        }

        private async Task<AnimeDetail?> FetchAnimeDetailAsync(
            string queryParam, ConcurrentDictionary<long, CacheEntry<AnimeDetail?>> cache, long cacheKey, string idKindForLogging, long idForLogging)
        {
            if (!IsConfigured)
            {
                return null;
            }

            EnsurePersistentCacheLoaded();

            if (cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < AnimeDetailCacheDuration)
            {
                return cached.Data;
            }

            try
            {
                var url = $"{BaseUrl}/anime?{queryParam}";
                using var response = await SendAsync(url);
                if (response == null || !response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var page = JsonSerializer.Deserialize<AnimeSearchResponse>(json, _jsonOptions);
                var detail = page?.Anime?.FirstOrDefault();

                cache[cacheKey] = new CacheEntry<AnimeDetail?>(DateTimeOffset.UtcNow, detail);
                PersistCacheToDiskSafe();
                return detail;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeSchedule: failed to resolve anime detail for {IdKind} id {Id}", idKindForLogging, idForLogging);
                return null;
            }
        }

        // GET /timetables/{sub|dub}?week=&year= — a single ISO week's airing schedule.
        public async Task<List<TimetableEntry>> GetTimetableAsync(string airType, int isoWeek, int year)
        {
            if (!IsConfigured)
            {
                return new List<TimetableEntry>();
            }

            var cacheKey = $"{airType}:{year}-W{isoWeek}";
            EnsurePersistentCacheLoaded();

            if (TimetableCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < TimetableCacheDuration)
            {
                return cached.Data;
            }

            try
            {
                var url = $"{BaseUrl}/timetables/{airType}?week={isoWeek}&year={year}";
                using var response = await SendAsync(url);
                if (response == null || !response.IsSuccessStatusCode)
                {
                    return new List<TimetableEntry>();
                }

                var json = await response.Content.ReadAsStringAsync();
                var entries = JsonSerializer.Deserialize<List<TimetableEntry>>(json, _jsonOptions) ?? new List<TimetableEntry>();

                TimetableCache[cacheKey] = new CacheEntry<List<TimetableEntry>>(DateTimeOffset.UtcNow, entries);
                PersistCacheToDiskSafe();
                return entries;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeSchedule: failed to fetch {AirType} timetable for {Year}-W{Week}", airType, year, isoWeek);
                return new List<TimetableEntry>();
            }
        }

        private async Task<HttpResponseMessage?> SendAsync(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return await _httpClient.SendAsync(request);
        }

        private void EnsurePersistentCacheLoaded()
        {
            if (_persistentCacheLoaded)
            {
                return;
            }

            lock (PersistentCacheLock)
            {
                if (_persistentCacheLoaded)
                {
                    return;
                }

                try
                {
                    LoadCacheFromDisk();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load AnimeSchedule cache from disk");
                }
                finally
                {
                    _persistentCacheLoaded = true;
                }
            }
        }

        private void LoadCacheFromDisk()
        {
            var path = GetPersistentCachePath();
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<CacheSnapshot>(json, _jsonOptions);
            if (snapshot == null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var kvp in snapshot.AnimeDetails ?? new Dictionary<long, CacheEntry<AnimeDetail?>>())
            {
                if (now - kvp.Value.CachedAt < AnimeDetailCacheDuration)
                {
                    AnimeDetailCache[kvp.Key] = kvp.Value;
                }
            }

            foreach (var kvp in snapshot.AnimeDetailsByAniDb ?? new Dictionary<long, CacheEntry<AnimeDetail?>>())
            {
                if (now - kvp.Value.CachedAt < AnimeDetailCacheDuration)
                {
                    AnimeDetailByAniDbCache[kvp.Key] = kvp.Value;
                }
            }

            foreach (var kvp in snapshot.Timetables ?? new Dictionary<string, CacheEntry<List<TimetableEntry>>>())
            {
                if (now - kvp.Value.CachedAt < TimetableCacheDuration)
                {
                    TimetableCache[kvp.Key] = kvp.Value;
                }
            }
        }

        private void PersistCacheToDiskSafe()
        {
            try
            {
                var snapshot = new CacheSnapshot
                {
                    AnimeDetails = AnimeDetailCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    AnimeDetailsByAniDb = AnimeDetailByAniDbCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    Timetables = TimetableCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                };

                var path = GetPersistentCachePath();
                lock (PersistentCacheLock)
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(path, JsonSerializer.Serialize(snapshot, _jsonOptions));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist AnimeSchedule cache to disk");
            }
        }

        private string GetPersistentCachePath()
        {
            if (!string.IsNullOrWhiteSpace(_persistentCachePath))
            {
                return _persistentCachePath!;
            }

            lock (PersistentCacheLock)
            {
                if (!string.IsNullOrWhiteSpace(_persistentCachePath))
                {
                    return _persistentCachePath!;
                }

                string folder;
                try
                {
                    var plugin = Plugin.Instance;
                    var dataFolderProperty = plugin?.GetType().GetProperty("DataFolderPath");
                    folder = dataFolderProperty?.GetValue(plugin) as string ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        throw new InvalidOperationException("DataFolderPath unavailable");
                    }

                    Directory.CreateDirectory(folder);
                }
                catch
                {
                    folder = Path.Combine(AppContext.BaseDirectory, "AnimeMultiSourceCache");
                    Directory.CreateDirectory(folder);
                }

                _persistentCachePath = Path.Combine(folder, "animeschedule-cache.json");
                return _persistentCachePath;
            }
        }

        private sealed record CacheEntry<T>(DateTimeOffset CachedAt, T Data);

        private sealed class CacheSnapshot
        {
            public Dictionary<long, CacheEntry<AnimeDetail?>>? AnimeDetails { get; set; }
            public Dictionary<long, CacheEntry<AnimeDetail?>>? AnimeDetailsByAniDb { get; set; }
            public Dictionary<string, CacheEntry<List<TimetableEntry>>>? Timetables { get; set; }
        }

        private sealed class AnimeSearchResponse
        {
            [JsonPropertyName("anime")]
            public List<AnimeDetail>? Anime { get; set; }
        }

        public sealed class AnimeDetail
        {
            [JsonPropertyName("route")]
            public string? Route { get; set; }

            [JsonPropertyName("names")]
            public AnimeNames? Names { get; set; }

            [JsonPropertyName("description")]
            public string? Description { get; set; }

            [JsonPropertyName("websites")]
            public AnimeWebsites? Websites { get; set; }

            // AnimeSchedule's website links are full URLs ("myanimelist.net/anime/61240/...",
            // "anidb.net/anime/19226", "anilist.co/anime/188139/..."), not raw ids - pull the
            // numeric id that follows "/anime/" out of each one.
            [JsonIgnore]
            public long? MalId => ExtractAnimeId(Websites?.Mal);

            [JsonIgnore]
            public long? AniListId => ExtractAnimeId(Websites?.AniList);

            [JsonIgnore]
            public long? AniDbId => ExtractAnimeId(Websites?.AniDb);

            private static readonly Regex AnimeIdPattern = new(@"/anime/(\d+)", RegexOptions.Compiled);

            private static long? ExtractAnimeId(string? url)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return null;
                }

                var match = AnimeIdPattern.Match(url);
                return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : null;
            }
        }

        public sealed class AnimeWebsites
        {
            [JsonPropertyName("mal")]
            public string? Mal { get; set; }

            [JsonPropertyName("aniList")]
            public string? AniList { get; set; }

            [JsonPropertyName("anidb")]
            public string? AniDb { get; set; }
        }

        public sealed class AnimeNames
        {
            [JsonPropertyName("romaji")]
            public string? Romaji { get; set; }

            [JsonPropertyName("english")]
            public string? English { get; set; }

            [JsonPropertyName("native")]
            public string? Native { get; set; }
        }

        public sealed class TimetableEntry
        {
            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("route")]
            public string? Route { get; set; }

            [JsonPropertyName("episodeDate")]
            public DateTimeOffset? EpisodeDate { get; set; }

            [JsonPropertyName("episodeNumber")]
            public int? EpisodeNumber { get; set; }
        }
    }
}
