using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>
    /// Poster and backdrop for anime movies, from AniList (cover and banner image) using the AniList id the metadata
    /// provider stored on the movie. Lets an "Anime Movies" library run without TheMovieDb.
    /// </summary>
    public class AnimeMultiSourceMovieImageProvider : IRemoteImageProvider
    {
        private readonly ILogger<AnimeMultiSourceMovieImageProvider> _logger;
        private readonly ApiService _apiService;
        private readonly HttpClient _httpClient;

        public AnimeMultiSourceMovieImageProvider(ILogger<AnimeMultiSourceMovieImageProvider> logger)
        {
            logger = PluginLog.Wrap(logger);
            _logger = logger;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler);
            _apiService = new ApiService(_httpClient, logger);
        }

        public string Name => $"{Constants.PluginName} AniList";

        public bool Supports(BaseItem item) => item is Movie;

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
            yield return ImageType.Backdrop;
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var images = new List<RemoteImageInfo>();
            if (ItemExclusion.IsExcluded(item, Plugin.GetConfigurationSafe(_logger)))
            {
                return images;
            }

            if (!long.TryParse(item.GetProviderId(Constants.AniListProviderId), out var aniListId) || aniListId <= 0)
            {
                return images;
            }

            var media = await _apiService.GetAniListMovieMediaAsync(aniListId).ConfigureAwait(false);
            if (media == null)
            {
                return images;
            }

            var cover = media.CoverImage?.ExtraLarge ?? media.CoverImage?.Large;
            if (!string.IsNullOrWhiteSpace(cover))
            {
                images.Add(new RemoteImageInfo { ProviderName = Name, Url = cover, Type = ImageType.Primary });
            }

            if (!string.IsNullOrWhiteSpace(media.BannerImage))
            {
                images.Add(new RemoteImageInfo { ProviderName = Name, Url = media.BannerImage, Type = ImageType.Backdrop });
            }

            return images;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetAsync(url, cancellationToken);
        }
    }
}
