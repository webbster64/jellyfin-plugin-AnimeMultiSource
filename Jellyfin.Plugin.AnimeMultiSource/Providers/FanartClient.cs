using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class FanartClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly string _personalApiKey;

        private const string BaseUrl = "https://webservice.fanart.tv/v3/tv";

        public FanartClient(HttpClient httpClient, ILogger logger, string personalApiKey)
        {
            _httpClient = httpClient;
            _logger = logger;
            _personalApiKey = personalApiKey ?? string.Empty;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public async Task<FanartTvResponse?> GetAsync(string tvdbId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tvdbId))
            {
                return null;
            }

            var url = $"{BaseUrl}/{tvdbId}?api_key={_personalApiKey}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Fanart.tv request failed for tvdb {TvdbId} with status {Status}", tvdbId, response.StatusCode);
                return null;
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<FanartTvResponse>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse Fanart.tv response for tvdb {TvdbId}", tvdbId);
                return null;
            }
        }

        public Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetAsync(url, cancellationToken);
        }
    }
}
