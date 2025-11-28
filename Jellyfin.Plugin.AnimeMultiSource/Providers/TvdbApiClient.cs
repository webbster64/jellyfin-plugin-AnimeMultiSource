using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class TvdbApiClient
    {
        private const string BaseUrl = "https://api4.thetvdb.com/v4";
        private static readonly TimeSpan EpisodeCacheDuration = TimeSpan.FromHours(6);
        private static readonly TimeSpan SeriesCacheDuration = TimeSpan.FromHours(6);

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly string _projectApiKey;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private readonly ConcurrentDictionary<int, TvdbEpisodeCacheEntry> _episodeCache = new();
        private readonly ConcurrentDictionary<int, TvdbSeriesCacheEntry> _seriesCache = new();

        private string? _token;
        private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

        public TvdbApiClient(HttpClient httpClient, ILogger logger, string projectApiKey)
        {
            _httpClient = httpClient;
            _logger = logger;
            _projectApiKey = projectApiKey ?? string.Empty;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public async Task<TvdbEpisode?> FindEpisodeAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken)
        {
            var episodes = await GetEpisodesForSeriesAsync(seriesId, cancellationToken);
            foreach (var episode in episodes)
            {
                if (episode.SeasonNumber == seasonNumber && episode.Number == episodeNumber)
                {
                    return episode;
                }
            }

            return null;
        }

        public async Task<List<TvdbEpisode>> GetEpisodesForSeriesAsync(int seriesId, CancellationToken cancellationToken)
        {
            if (_episodeCache.TryGetValue(seriesId, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < EpisodeCacheDuration)
            {
                _logger.LogDebug("TVDB cache hit for series {SeriesId}", seriesId);
                return new List<TvdbEpisode>(cached.Episodes);
            }

            var episodes = new List<TvdbEpisode>();
            var nextUrl = $"{BaseUrl}/series/{seriesId}/episodes/default?page=0";
            var pageGuard = 0;

            while (!string.IsNullOrEmpty(nextUrl) && pageGuard < 20)
            {
                pageGuard++;
                var response = await SendAuthorizedAsync(nextUrl, cancellationToken);
                if (response == null)
                {
                    break;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TVDB request for {Url} failed with status {Status}", nextUrl, response.StatusCode);
                    break;
                }

                var body = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<TvdbEpisodeListResponse>(body, _jsonOptions);
                if (data?.Data?.Episodes != null)
                {
                    episodes.AddRange(data.Data.Episodes);
                    _logger.LogDebug("Fetched {Count} episodes from {Url}", data.Data.Episodes.Count, nextUrl);
                }

                nextUrl = data?.Links?.Next;
            }

            _episodeCache[seriesId] = new TvdbEpisodeCacheEntry(DateTimeOffset.UtcNow, new List<TvdbEpisode>(episodes));
            _logger.LogInformation("Cached {Count} TVDB episodes for series {SeriesId}", episodes.Count, seriesId);

            return episodes;
        }

        public Task<HttpResponseMessage> GetImageAsync(string url, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            return _httpClient.SendAsync(request, cancellationToken);
        }

        public async Task<TvdbTranslation?> GetEpisodeTranslationAsync(int episodeId, string language, CancellationToken cancellationToken)
        {
            var url = $"{BaseUrl}/episodes/{episodeId}/translations/{language}";
            var response = await SendAuthorizedAsync(url, cancellationToken);
            if (response == null || !response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TVDB translation request failed for episode {EpisodeId} lang {Language} (status: {Status})",
                    episodeId, language, response?.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var translation = JsonSerializer.Deserialize<TvdbTranslationResponse>(json, _jsonOptions);
            return translation?.Data;
        }

        public async Task<TvdbEpisode?> GetEpisodeByIdAsync(int episodeId, CancellationToken cancellationToken)
        {
            var url = $"{BaseUrl}/episodes/{episodeId}";
            var response = await SendAuthorizedAsync(url, cancellationToken);
            if (response == null || !response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TVDB episode lookup failed for id {EpisodeId} (status: {Status})", episodeId, response?.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<TvdbEpisodeResponse>(json, _jsonOptions);
            return data?.Data;
        }

        public async Task<TvdbSeriesExtended?> GetSeriesExtendedAsync(int seriesId, CancellationToken cancellationToken)
        {
            if (_seriesCache.TryGetValue(seriesId, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < SeriesCacheDuration)
            {
                return cached.Series;
            }

            var url = $"{BaseUrl}/series/{seriesId}/extended";
            var response = await SendAuthorizedAsync(url, cancellationToken);
            if (response == null || !response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TVDB series extended request failed for id {SeriesId} (status: {Status})", seriesId, response?.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<TvdbSeriesExtendedResponse>(json, _jsonOptions);
            if (data?.Data != null)
            {
                _seriesCache[seriesId] = new TvdbSeriesCacheEntry(DateTimeOffset.UtcNow, data.Data);
            }

            return data?.Data;
        }

        private async Task<HttpResponseMessage?> SendAuthorizedAsync(string url, CancellationToken cancellationToken)
        {
            var token = await GetTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("TVDB token unavailable; skipping request to {Url}", url);
                return null;
            }

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogInformation("TVDB token expired, refreshing token and retrying {Url}", url);
                _token = null;

                token = await GetTokenAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return response;
                }

                request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                response = await _httpClient.SendAsync(request, cancellationToken);
            }

            return response;
        }

        private async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_token) && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return _token;
            }

            if (string.IsNullOrWhiteSpace(_projectApiKey))
            {
                _logger.LogWarning("TVDB project API key is missing; cannot authenticate");
                return null;
            }

            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                if (!string.IsNullOrWhiteSpace(_token) && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
                {
                    return _token;
                }

                var payload = JsonSerializer.Serialize(new { apikey = _projectApiKey });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{BaseUrl}/login", content, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TVDB login failed with status {Status}", response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var login = JsonSerializer.Deserialize<TvdbLoginResponse>(json, _jsonOptions);
                _token = login?.Data?.Token;
                _tokenExpiry = DateTimeOffset.UtcNow.AddHours(12);

                if (string.IsNullOrWhiteSpace(_token))
                {
                    _logger.LogWarning("TVDB login returned no token");
                }
                else
                {
                    _logger.LogInformation("Obtained TVDB token; expires at {Expiry}", _tokenExpiry.ToString("u"));
                }

                return _token;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acquiring TVDB token");
                return null;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private sealed record TvdbEpisodeCacheEntry(DateTimeOffset CachedAt, List<TvdbEpisode> Episodes);
        private sealed record TvdbSeriesCacheEntry(DateTimeOffset CachedAt, TvdbSeriesExtended Series);
    }
}
