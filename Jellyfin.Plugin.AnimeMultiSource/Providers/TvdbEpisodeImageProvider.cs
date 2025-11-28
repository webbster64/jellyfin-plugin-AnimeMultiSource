using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class TvdbEpisodeImageProvider : IRemoteImageProvider
    {
        private readonly ILogger<TvdbEpisodeImageProvider> _logger;
        private readonly TvdbApiClient _tvdbClient;

        public TvdbEpisodeImageProvider(ILogger<TvdbEpisodeImageProvider> logger)
        {
            _logger = logger;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var httpClient = new HttpClient(handler);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-AnimeMultiSource-Plugin/1.0");

            _tvdbClient = new TvdbApiClient(httpClient, logger, Constants.TvdbProjectApiKey);
        }

        public string Name => $"{Constants.PluginName} TVDB Episode Images";

        public bool Supports(BaseItem item)
        {
            return item is Episode;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            if (item is not Episode episode)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            var tvdbId = episode.GetProviderId(MetadataProvider.Tvdb);
            if (string.IsNullOrWhiteSpace(tvdbId) || !int.TryParse(tvdbId, out var episodeId))
            {
                _logger.LogDebug("Episode {Name} missing TVDB provider id; skipping image lookup", episode.Name);
                return Array.Empty<RemoteImageInfo>();
            }

            var tvdbEpisode = await _tvdbClient.GetEpisodeByIdAsync(episodeId, cancellationToken);
            if (tvdbEpisode == null || string.IsNullOrEmpty(tvdbEpisode.Image))
            {
                _logger.LogDebug("No image found for TVDB episode id {EpisodeId}", episodeId);
                return Array.Empty<RemoteImageInfo>();
            }

            return new[]
            {
                new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = tvdbEpisode.Image,
                    Type = ImageType.Primary
                }
            };
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _tvdbClient.GetImageAsync(url, cancellationToken);
        }
    }
}
