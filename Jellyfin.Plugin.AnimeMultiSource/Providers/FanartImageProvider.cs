using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class FanartImageProvider : IRemoteImageProvider
    {
        private readonly ILogger<FanartImageProvider> _logger;
        private readonly FanartClient _fanartClient;
        private readonly TvdbApiClient _tvdbClient;
        private readonly PluginConfiguration _config;

        public FanartImageProvider(ILogger<FanartImageProvider> logger)
        {
            _logger = logger;
            _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var httpClient = new HttpClient(handler);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-AnimeMultiSource-Plugin/1.0");

            _fanartClient = new FanartClient(httpClient, logger, _config.PersonalApiKey ?? string.Empty);
            _tvdbClient = new TvdbApiClient(httpClient, logger, Constants.TvdbProjectApiKey);
        }

        public string Name => $"{Constants.PluginName} Fanart.tv";

        public bool Supports(BaseItem item)
        {
            return item is Series || item is Season;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
            yield return ImageType.Backdrop;
            yield return ImageType.Banner;
            yield return ImageType.Logo;
            yield return ImageType.Art;
            yield return ImageType.Thumb;
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var version = typeof(FanartImageProvider).Assembly.GetName().Version?.ToString() ?? "unknown";
            _logger.LogInformation("{Provider} v{Version} GetImages for {Name} ({ItemType})", Name, version, item.Name, item.GetType().Name);

            var tvdbId = GetTvdbId(item);
            if (string.IsNullOrWhiteSpace(tvdbId))
            {
                _logger.LogInformation("Missing TVDB id; cannot fetch Fanart.tv artwork for {Name}", item.Name);
                return Array.Empty<RemoteImageInfo>();
            }

            var seasonNumber = item is Season season ? season.IndexNumber : null;
            if (item is Season && !seasonNumber.HasValue)
            {
                seasonNumber = TryParseSeasonNumber(item.Name);
                if (!seasonNumber.HasValue)
                {
                    _logger.LogInformation("Season image lookup skipped: no season number for {Name} (TVDB {TvdbId})", item.Name, tvdbId);
                }
            }

            TvdbSeriesExtended? tvdbSeries = null;
            int? tvdbSeriesId = null;
            if (int.TryParse(tvdbId, out var parsedSeriesId))
            {
                tvdbSeriesId = parsedSeriesId;
                tvdbSeries = await _tvdbClient.GetSeriesExtendedAsync(parsedSeriesId, cancellationToken);
            }
            FanartTvResponse? fanart = null;
            if (!string.IsNullOrWhiteSpace(_config.PersonalApiKey))
            {
                fanart = await _fanartClient.GetAsync(tvdbId, cancellationToken);
            }

            var images = new List<RemoteImageInfo>();

            // Series-level images
            if (item is Series)
            {
                var seriesImages = new List<RemoteImageInfo>();

                if (fanart != null)
                {
                    seriesImages.AddRange(ToImageInfos(fanart.TvPosters, ImageType.Primary));
                    seriesImages.AddRange(ToImageInfos(fanart.TvBanners, ImageType.Banner));
                    seriesImages.AddRange(ToImageInfos(fanart.HdTvLogos.Any() ? fanart.HdTvLogos : fanart.ClearLogos, ImageType.Logo));
                    seriesImages.AddRange(ToImageInfos(fanart.ClearArt, ImageType.Art));
                    seriesImages.AddRange(ToImageInfos(fanart.TvThumbs, ImageType.Thumb));
                }

                if (tvdbSeriesId.HasValue)
                {
                    var tvdbSeriesImages = GetTvdbSeriesImages(tvdbSeriesId.Value, tvdbSeries);
                    if (tvdbSeriesImages.Count > 0)
                    {
                        _logger.LogInformation("Collected {Count} TVDB series images for {Name}", tvdbSeriesImages.Count, item.Name);
                        seriesImages.AddRange(tvdbSeriesImages);
                    }
                }

                if (seriesImages.Count > 0)
                {
                    images.AddRange(seriesImages
                        .GroupBy(img => img.Url, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First()));
                }

                // Series backdrops with fallback to TVDB if needed
                var target = _config.FanartMaxBackdrops <= 0 ? int.MaxValue : _config.FanartMaxBackdrops;
                var effectiveTarget = target == int.MaxValue ? 0 : target;
                var targetLog = string.Format("Backdrop target for {0}: {1}", item.Name, effectiveTarget);
                _logger.LogInformation("{Message}", (object)targetLog);

                var fanartBackdrops = fanart != null
                    ? FilterBackdrops(ToImageInfos(fanart.ShowBackgrounds, ImageType.Backdrop))
                    : new List<RemoteImageInfo>();

                var tvdbBackdrops = GetTvdbBackdrops(tvdbSeriesId, tvdbSeries)
                    .OrderByDescending(b => b.CommunityRating ?? 0)
                    .ToList();

                var combined = fanartBackdrops
                    .Concat(tvdbBackdrops)
                    .GroupBy(b => b.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .Take(target == int.MaxValue ? int.MaxValue : target)
                    .ToList();

                var fanartCount = fanartBackdrops.Count;
                var tvdbCount = tvdbBackdrops.Count;
                var combinedCount = combined.Count;
                var combinedLog = string.Format(
                    "Collected {0} fanart.tv backdrops, {1} TVDB backdrops; returning {2} for {3}",
                    fanartCount, tvdbCount, combinedCount, item.Name);
                _logger.LogInformation("{Message}", (object)combinedLog);
                images.AddRange(combined);
            }

            // Season-level images
            if (item is Season && seasonNumber.HasValue)
            {
                _logger.LogInformation("Fetching season images for {Name} S{SeasonNumber} (TVDB {TvdbId})", item.Name, seasonNumber, tvdbId);
                var seasonImages = new List<RemoteImageInfo>();

                // TVDB primary/banners first (main source)
                var displayOrder = (item as Season)?.Series?.DisplayOrder;
                if (string.IsNullOrWhiteSpace(displayOrder))
                {
                    displayOrder = "official";
                }
                _logger.LogInformation("Using display order '{DisplayOrder}' for season images on {Name}", displayOrder, item.Name);
                seasonImages.AddRange(await GetTvdbSeasonImagesAsync(tvdbId, seasonNumber.Value, displayOrder!, cancellationToken, tvdbSeries));

                // Fanart.tv as fallback
                if (fanart != null)
                {
                    seasonImages.AddRange(ToImageInfos(FilterSeason(fanart.SeasonPosters, seasonNumber.Value), ImageType.Primary, seasonNumber.Value));
                    seasonImages.AddRange(ToImageInfos(FilterSeason(fanart.SeasonBanners, seasonNumber.Value), ImageType.Banner, seasonNumber.Value));
                    seasonImages.AddRange(ToImageInfos(FilterSeason(fanart.SeasonThumbs, seasonNumber.Value), ImageType.Thumb, seasonNumber.Value));
                }

                _logger.LogInformation("Season images collected for {Name} S{SeasonNumber}: {Count}", item.Name, seasonNumber, seasonImages.Count);
                images.AddRange(seasonImages);
            }

            return images;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _fanartClient.GetImageResponseAsync(url, cancellationToken);
        }

        private string? GetTvdbId(BaseItem item)
        {
            var id = item.GetProviderId(MetadataProvider.Tvdb);
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }

            if (item is Season season && season.Series != null)
            {
                id = season.Series.GetProviderId(MetadataProvider.Tvdb);
            }

            return id;
        }

        private int? TryParseSeasonNumber(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // Match patterns like "Season 1", "S1", "Season 02"
            var match = Regex.Match(name, @"(?:Season|S)\s*(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number))
            {
                return number;
            }

            // Handle "Specials" as season 0
            if (name.Trim().Equals("Specials", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return null;
        }

        private List<RemoteImageInfo> ToImageInfos(IEnumerable<FanartImage> source, ImageType type, int? seasonNumber = null)
        {
            return source
                .Where(img => !string.IsNullOrWhiteSpace(img.Url))
                .Select(img => new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = img.Url!,
                    Type = type
                })
                .ToList();
        }

        private IEnumerable<FanartImage> FilterSeason(IEnumerable<FanartSeasonImage> images, int seasonNumber)
        {
            var seasonStr = seasonNumber.ToString();
            return images.Where(i => string.Equals(i.Season, seasonStr, StringComparison.OrdinalIgnoreCase));
        }

        private List<RemoteImageInfo> GetTvdbBackdrops(int? seriesId, TvdbSeriesExtended? series)
        {
            if (!seriesId.HasValue || series?.Artworks == null)
            {
                return new List<RemoteImageInfo>();
            }

            var totalArtworks = series.Artworks.Count;
            var backdropCandidates = series.Artworks
                .Where(a => IsBackdropType(a))
                .Where(a => !string.IsNullOrWhiteSpace(a.Image))
                .ToList();

            var backdrops = backdropCandidates
                .Where(PassBackdropQuality)
                .Select(a => new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = a.Image!,
                    Type = ImageType.Backdrop,
                    Width = a.Width,
                    Height = a.Height,
                    CommunityRating = ToRating(a.Score)
                })
                .ToList();

            if (backdrops.Count == 0 && backdropCandidates.Count > 0 && (_config.BackdropMinWidth > 0 || _config.BackdropMinHeight > 0 || _config.BackdropMinAspectRatio > 0))
            {
                _logger.LogInformation("TVDB backdrops for series {SeriesId}: relaxing quality filter (had {CandidateCount} candidates, none passed)", seriesId, backdropCandidates.Count);
                backdrops = backdropCandidates
                    .Select(a => new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Url = a.Image!,
                        Type = ImageType.Backdrop,
                        Width = a.Width,
                        Height = a.Height,
                        CommunityRating = ToRating(a.Score)
                    })
                    .ToList();
            }

            _logger.LogInformation("TVDB backdrops for series {SeriesId}: kept {Kept} of {Total} artworks after type/quality filter", seriesId, backdrops.Count, totalArtworks);

            return backdrops;
        }

        private List<RemoteImageInfo> GetTvdbSeriesImages(int seriesId, TvdbSeriesExtended? series)
        {
            if (series?.Artworks == null)
            {
                return new List<RemoteImageInfo>();
            }

            var list = new List<RemoteImageInfo>();

            list.AddRange(series.Artworks.Where(a => a.Type == 2 && !string.IsNullOrWhiteSpace(a.Image)).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = a.Image!,
                Type = ImageType.Primary,
                Width = a.Width,
                Height = a.Height,
                CommunityRating = ToRating(a.Score)
            }));

            list.AddRange(series.Artworks.Where(a => a.Type == 1 && !string.IsNullOrWhiteSpace(a.Image)).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = a.Image!,
                Type = ImageType.Banner,
                Width = a.Width,
                Height = a.Height,
                CommunityRating = ToRating(a.Score)
            }));

            list.AddRange(series.Artworks.Where(a => a.Type == 23 && !string.IsNullOrWhiteSpace(a.Image)).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = a.Image!,
                Type = ImageType.Logo,
                Width = a.Width,
                Height = a.Height,
                CommunityRating = ToRating(a.Score)
            }));

            list.AddRange(series.Artworks.Where(a => a.Type == 22 && !string.IsNullOrWhiteSpace(a.Image)).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = a.Image!,
                Type = ImageType.Art,
                Width = a.Width,
                Height = a.Height,
                CommunityRating = ToRating(a.Score)
            }));

            _logger.LogInformation("TVDB series art for {SeriesId}: {PosterCount} posters, {BannerCount} banners, {LogoCount} logos, {ArtCount} clearart",
                seriesId, list.Count(i => i.Type == ImageType.Primary), list.Count(i => i.Type == ImageType.Banner), list.Count(i => i.Type == ImageType.Logo), list.Count(i => i.Type == ImageType.Art));

            return list;
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetTvdbSeasonImagesAsync(string tvdbId, int seasonNumber, string displayOrder, CancellationToken cancellationToken, TvdbSeriesExtended? series = null)
        {
            if (!int.TryParse(tvdbId, out var seriesId))
            {
                return Array.Empty<RemoteImageInfo>();
            }

            series ??= await _tvdbClient.GetSeriesExtendedAsync(seriesId, cancellationToken);
            if (series?.Artworks == null || series.Seasons == null)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            var seasonsMatchingNumber = series.Seasons
                .Where(s => s.Number == seasonNumber)
                .ToList();

            if (seasonsMatchingNumber.Count == 0)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            var selectedSeason = seasonsMatchingNumber
                .FirstOrDefault(s => string.Equals(s.Type?.Type, displayOrder, StringComparison.OrdinalIgnoreCase))
                ?? seasonsMatchingNumber.FirstOrDefault(s => string.Equals(s.Type?.Type, "official", StringComparison.OrdinalIgnoreCase))
                ?? seasonsMatchingNumber.First();

            var seasonId = selectedSeason.Id;
            _logger.LogInformation("Selected TVDB season {SeasonId} for S{SeasonNumber} using type '{Type}' (display order: {DisplayOrder})",
                seasonId, seasonNumber, selectedSeason.Type?.Type ?? "unknown", displayOrder);

            var list = new List<RemoteImageInfo>();

            var season = selectedSeason;

            var posters = series.Artworks.Where(a => a.Type == 7 && a.SeasonId == seasonId);
            var banners = series.Artworks.Where(a => a.Type == 6 && a.SeasonId == seasonId);

            list.AddRange(posters.Where(a => !string.IsNullOrWhiteSpace(a.Image)).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = a.Image!,
                Type = ImageType.Primary,
                Width = a.Width,
                Height = a.Height,
                CommunityRating = ToRating(a.Score)
            }));

            list.AddRange(banners.Where(a => !string.IsNullOrWhiteSpace(a.Image)).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = a.Image!,
                Type = ImageType.Banner,
                Width = a.Width,
                Height = a.Height,
                CommunityRating = ToRating(a.Score)
            }));

            // If no poster found, use season.Image from seasons list as a last resort
            if (!list.Any(l => l.Type == ImageType.Primary) && season != null && !string.IsNullOrWhiteSpace(season.Image))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = season.Image,
                    Type = ImageType.Primary
                });
            }

            return list;
        }

        private bool PassBackdropQuality(TvdbArtwork artwork)
        {
            if (artwork.Width.HasValue && artwork.Width.Value < _config.BackdropMinWidth && _config.BackdropMinWidth > 0)
            {
                return false;
            }

            if (artwork.Height.HasValue && artwork.Height.Value < _config.BackdropMinHeight && _config.BackdropMinHeight > 0)
            {
                return false;
            }

            if (_config.BackdropMinAspectRatio > 0 &&
                artwork.Width.HasValue && artwork.Height.HasValue && artwork.Height.Value > 0)
            {
                var aspect = artwork.Width.Value / (double)artwork.Height.Value;
                if (aspect < _config.BackdropMinAspectRatio)
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsBackdropType(TvdbArtwork artwork)
        {
            // TVDB artwork types: 3 = series/season background in the v4 API.
            return artwork.Type == 3;
        }

        private List<RemoteImageInfo> FilterBackdrops(List<RemoteImageInfo> images)
        {
            if (_config.BackdropMinWidth <= 0 && _config.BackdropMinHeight <= 0 && _config.BackdropMinAspectRatio <= 0)
            {
                return images;
            }

            return images.Where(img =>
            {
                if (_config.BackdropMinWidth > 0 && img.Width.HasValue && img.Width.Value < _config.BackdropMinWidth)
                {
                    return false;
                }

                if (_config.BackdropMinHeight > 0 && img.Height.HasValue && img.Height.Value < _config.BackdropMinHeight)
                {
                    return false;
                }

                if (_config.BackdropMinAspectRatio > 0 && img.Width.HasValue && img.Height.HasValue && img.Height.Value > 0)
                {
                    var aspect = img.Width.Value / (double)img.Height.Value;
                    if (aspect < _config.BackdropMinAspectRatio)
                    {
                        return false;
                    }
                }

                return true;
            }).ToList();
        }

        private double? ToRating(int? score)
        {
            if (!score.HasValue)
            {
                return null;
            }

            // TVDB scores are large integers; normalize to 0-10 range
            return Math.Min(10, Math.Round(score.Value / 10000.0, 2));
        }
    }
}
