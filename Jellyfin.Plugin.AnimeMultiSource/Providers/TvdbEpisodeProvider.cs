using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class TvdbEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>
    {
        private readonly ILogger<TvdbEpisodeProvider> _logger;
        private readonly TvdbApiClient _tvdbClient;

        public TvdbEpisodeProvider(ILogger<TvdbEpisodeProvider> logger)
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

        public string Name => $"{Constants.PluginName} TVDB Episodes";

        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Episode>();

            if (!info.ParentIndexNumber.HasValue || !info.IndexNumber.HasValue)
            {
                _logger.LogDebug("Skipping TVDB lookup for {Name}: missing season/episode numbers", info.Name);
                return result;
            }

            if (!TryGetTvdbSeriesId(info, out var tvdbSeriesId))
            {
                _logger.LogDebug("Skipping TVDB lookup for {Name}: missing TVDB series id", info.Name);
                return result;
            }

            var tvdbEpisode = await _tvdbClient.FindEpisodeAsync(tvdbSeriesId, info.ParentIndexNumber.Value, info.IndexNumber.Value, cancellationToken);
            if (tvdbEpisode == null)
            {
                _logger.LogWarning("TVDB episode not found for series {SeriesId} S{Season}E{Episode}", tvdbSeriesId, info.ParentIndexNumber, info.IndexNumber);
                return result;
            }

            var engTranslation = await _tvdbClient.GetEpisodeTranslationAsync(tvdbEpisode.Id, "eng", cancellationToken);

            var episode = new Episode
            {
                Name = engTranslation?.Name ?? tvdbEpisode.Name ?? info.Name,
                OriginalTitle = tvdbEpisode.Name,
                IndexNumber = tvdbEpisode.Number ?? info.IndexNumber,
                ParentIndexNumber = tvdbEpisode.SeasonNumber ?? info.ParentIndexNumber,
                Overview = engTranslation?.Overview ?? tvdbEpisode.Overview
            };

            if (DateTime.TryParse(tvdbEpisode.Aired, out var airDate))
            {
                episode.PremiereDate = airDate;
                episode.ProductionYear = airDate.Year;
            }

            episode.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            episode.SetProviderId(MetadataProvider.Tvdb, tvdbEpisode.Id.ToString());

            result.Item = episode;
            result.HasMetadata = true;

            if (!string.IsNullOrEmpty(tvdbEpisode.Image))
            {
                result.RemoteImages = new List<(string Url, ImageType Type)>
                {
                    (tvdbEpisode.Image, ImageType.Primary)
                };
            }

            _logger.LogInformation("TVDB episode metadata set for series {SeriesId} S{Season}E{Episode} - {Title}", tvdbSeriesId, episode.ParentIndexNumber, episode.IndexNumber, episode.Name);

            return result;
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _tvdbClient.GetImageAsync(url, cancellationToken);
        }

        private bool TryGetTvdbSeriesId(EpisodeInfo info, out int seriesId)
        {
            seriesId = 0;

            if (info.SeriesProviderIds != null &&
                TryGetValueIgnoreCase(info.SeriesProviderIds, MetadataProvider.Tvdb.ToString(), out var rawSeriesId) &&
                int.TryParse(rawSeriesId, out seriesId))
            {
                return true;
            }

            if (info.ProviderIds != null &&
                TryGetValueIgnoreCase(info.ProviderIds, MetadataProvider.Tvdb.ToString(), out rawSeriesId) &&
                int.TryParse(rawSeriesId, out seriesId))
            {
                return true;
            }

            return false;
        }

        private bool TryGetValueIgnoreCase(IDictionary<string, string> dictionary, string key, out string value)
        {
            foreach (var kvp in dictionary)
            {
                if (kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }
    }
}
