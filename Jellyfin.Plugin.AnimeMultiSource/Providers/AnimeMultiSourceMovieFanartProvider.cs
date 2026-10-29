using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>
    /// Fanart.tv artwork (posters, backdrops, logos, clear art, banners, thumbs, discs) for anime movies. Looked up by the
    /// TMDb id (or IMDb id) that the movie metadata provider stored, so it needs the personal Fanart.tv API key from the
    /// plugin settings, the same one the series provider uses.
    /// </summary>
    public class AnimeMultiSourceMovieFanartProvider : IRemoteImageProvider
    {
        private readonly ILogger<AnimeMultiSourceMovieFanartProvider> _logger;
        private readonly FanartClient _fanartClient;
        private readonly PluginConfiguration _config;

        public AnimeMultiSourceMovieFanartProvider(ILogger<AnimeMultiSourceMovieFanartProvider> logger)
        {
            logger = PluginLog.Wrap(logger);
            _logger = logger;
            _config = Plugin.GetConfigurationSafe(_logger);

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var httpClient = new HttpClient(handler);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-AnimeMultiSource-Plugin/1.0");
            _fanartClient = new FanartClient(httpClient, logger, _config.PersonalApiKey ?? string.Empty);
        }

        // Distinct from the series provider's name: Jellyfin looks image providers up by name.
        public string Name => $"{Constants.PluginName} Fanart.tv Movies";

        public bool Supports(BaseItem item) => item is Movie;

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
            yield return ImageType.Backdrop;
            yield return ImageType.Banner;
            yield return ImageType.Logo;
            yield return ImageType.Art;
            yield return ImageType.Thumb;
            yield return ImageType.Disc;
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var images = new List<RemoteImageInfo>();
            if (ItemExclusion.IsExcluded(item, Plugin.GetConfigurationSafe(_logger)))
            {
                return images;
            }

            if (string.IsNullOrWhiteSpace(_config.PersonalApiKey))
            {
                _logger.LogDebug("No Fanart.tv API key configured; skipping artwork for {Name}", item.Name);
                return images;
            }

            // TMDb first, then IMDb: fanart.tv knows a film by either, and only one of them may be on the item.
            var ids = new List<string>();
            var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrWhiteSpace(tmdb))
            {
                ids.Add(tmdb);
            }

            var imdb = item.GetProviderId(MetadataProvider.Imdb);
            if (!string.IsNullOrWhiteSpace(imdb))
            {
                ids.Add(imdb);
            }

            if (ids.Count == 0)
            {
                _logger.LogInformation("No TMDb or IMDb id on {Name}; cannot fetch Fanart.tv artwork", item.Name);
                return images;
            }

            FanartMovieResponse? fanart = null;
            foreach (var id in ids)
            {
                fanart = await _fanartClient.GetMovieAsync(id, cancellationToken).ConfigureAwait(false);
                if (fanart != null)
                {
                    break;
                }
            }

            if (fanart == null)
            {
                _logger.LogInformation("Fanart.tv has no artwork for {Name} ({Ids})", item.Name, string.Join(", ", ids));
                return images;
            }

            images.AddRange(ToImageInfos(fanart.MoviePosters, ImageType.Primary));
            images.AddRange(ToImageInfos(fanart.HdMovieLogos.Count > 0 ? fanart.HdMovieLogos : fanart.MovieLogos, ImageType.Logo));
            images.AddRange(ToImageInfos(fanart.HdMovieClearArt.Count > 0 ? fanart.HdMovieClearArt : fanart.MovieArt, ImageType.Art));
            images.AddRange(ToImageInfos(fanart.MovieBanners, ImageType.Banner));
            images.AddRange(ToImageInfos(fanart.MovieThumbs, ImageType.Thumb));
            images.AddRange(ToImageInfos(fanart.MovieDiscs, ImageType.Disc));

            var maxBackdrops = _config.FanartMaxBackdrops <= 0 ? int.MaxValue : _config.FanartMaxBackdrops;
            images.AddRange(ToImageInfos(fanart.MovieBackgrounds, ImageType.Backdrop).Take(maxBackdrops));

            _logger.LogInformation("Fanart.tv returned {Count} images for {Name}", images.Count, item.Name);
            return images;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _fanartClient.GetImageResponseAsync(url, cancellationToken);
        }

        private IEnumerable<RemoteImageInfo> ToImageInfos(IEnumerable<FanartImage> source, ImageType type)
        {
            var wanted = ParseLanguages(_config.FanartLanguages);
            foreach (var image in source)
            {
                if (string.IsNullOrWhiteSpace(image.Url))
                {
                    continue;
                }

                var language = string.IsNullOrWhiteSpace(image.Language) ? "00" : image.Language.Trim();
                if (wanted.Count > 0 && !wanted.Contains(language))
                {
                    continue;
                }

                yield return new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = image.Url!,
                    Type = type,
                    // "00" is fanart.tv's "no language"; Jellyfin expects null for that.
                    Language = language == "00" ? null : language
                };
            }
        }

        // Same rules as the series provider: empty or "all" means no filter, "none" means language-less images.
        private static HashSet<string> ParseLanguages(string? configValue)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(configValue) || configValue.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            foreach (var part in configValue.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var value = part.Trim();
                result.Add(value.Equals("none", StringComparison.OrdinalIgnoreCase) ? "00" : value);
            }

            return result;
        }
    }
}
