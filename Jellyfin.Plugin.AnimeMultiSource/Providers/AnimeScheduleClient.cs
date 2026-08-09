using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        private static readonly TimeSpan RouteCacheDuration = TimeSpan.FromDays(7);
        private static readonly TimeSpan TimetableCacheDuration = TimeSpan.FromHours(6);
        private static readonly ConcurrentDictionary<long, CacheEntry<string?>> RouteCache = new();
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
            if (!IsConfigured)
            {
                return null;
            }

            EnsurePersistentCacheLoaded();

            if (RouteCache.TryGetValue(aniListId, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < RouteCacheDuration)
            {
                return cached.Data;
            }

            try
            {
                var url = $"{BaseUrl}/anime?anilist-ids={aniListId}";
                using var response = await SendAsync(url);
                if (response == null || !response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var page = JsonSerializer.Deserialize<AnimeSearchResponse>(json, _jsonOptions);
                var route = page?.Anime?.FirstOrDefault()?.Route;

                RouteCache[aniListId] = new CacheEntry<string?>(DateTimeOffset.UtcNow, route);
                PersistCacheToDiskSafe();
                return route;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeSchedule: failed to resolve route for AniList id {AniListId}", aniListId);
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
            foreach (var kvp in snapshot.Routes ?? new Dictionary<long, CacheEntry<string?>>())
            {
                if (now - kvp.Value.CachedAt < RouteCacheDuration)
                {
                    RouteCache[kvp.Key] = kvp.Value;
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
                    Routes = RouteCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
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
            public Dictionary<long, CacheEntry<string?>>? Routes { get; set; }
            public Dictionary<string, CacheEntry<List<TimetableEntry>>>? Timetables { get; set; }
        }

        private sealed class AnimeSearchResponse
        {
            [JsonPropertyName("anime")]
            public List<AnimeSearchResult>? Anime { get; set; }
        }

        private sealed class AnimeSearchResult
        {
            [JsonPropertyName("route")]
            public string? Route { get; set; }
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
