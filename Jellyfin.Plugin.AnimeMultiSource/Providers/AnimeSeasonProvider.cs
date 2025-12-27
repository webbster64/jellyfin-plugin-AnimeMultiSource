using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class AnimeSeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>
    {
        private readonly ILogger<AnimeSeasonProvider> _logger;
        private readonly ApiService _apiService;
        private readonly PlexMatchParser _plexMatchParser;
        private readonly AnimeListMapper _animeListMapper;
        private readonly TagFilterService _tagFilterService;
        private PluginConfiguration _config;

        public AnimeSeasonProvider(ILogger<AnimeSeasonProvider> logger)
        {
            _logger = logger;
            _config = Plugin.GetConfigurationSafe(_logger);

            var httpClientHandler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };

            var httpClient = new HttpClient(httpClientHandler);
            _apiService = new ApiService(httpClient, logger);
            _plexMatchParser = new PlexMatchParser(logger);
            _animeListMapper = new AnimeListMapper(httpClient, logger);
            _tagFilterService = new TagFilterService();
        }

        public string Name => $"{Constants.PluginName} Season";

        public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Season>();
            _config = Plugin.GetConfigurationSafe(_logger);
            _logger.LogInformation("=== SEASON PROVIDER CALL for: {SeasonName} (Season {SeasonNumber}, Path: {Path}) ===",
                info.Name, info.IndexNumber, info.Path);

            if (!info.IndexNumber.HasValue || info.IndexNumber.Value < 0)
            {
                _logger.LogDebug("Skipping season metadata: invalid season number for {Name}", info.Name);
                return result;
            }

            var baseAniListId = await ResolveRootAniListIdAsync(info);
            var seasonNumber = info.IndexNumber.Value;
            if (!baseAniListId.HasValue)
            {
                _logger.LogWarning("Skipping season metadata: unable to resolve AniList root id for {Name}", info.Name);
                return result;
            }

            // For certain series, defer later seasons to TVDB/other providers to avoid bad AniList chains.
            if (baseAniListId.HasValue && AnimeMultiSourceService.ShouldDeferSeasonToTvdb(baseAniListId.Value, seasonNumber))
            {
                _logger.LogInformation("Skipping season metadata for {Name} S{SeasonNumber}: deferred to TVDB/other providers (override rule)", info.Name, seasonNumber);
                return result;
            }

            _logger.LogInformation("Fetching season metadata for {Name} S{SeasonNumber} using AniList base ID {AniListId}", info.Name, seasonNumber, baseAniListId);

            var seasonDetail = await _apiService.GetSeasonByNumberAsync(baseAniListId.Value, seasonNumber);
            if (seasonDetail == null)
            {
                if (_config.SeasonTitleFormat == SeasonTitleFormatType.Numbered)
                {
                    var seasonNameFallback = seasonNumber <= 0 ? "Specials" : $"Season {seasonNumber}";
                    var sortNameFallback = $"Season {seasonNumber:00}";
                    _logger.LogWarning(
                        "No season detail found for {Name} S{SeasonNumber} (AniList base {AniListId}); applying numbered fallback title",
                        info.Name, seasonNumber, baseAniListId);

                    var fallbackSeason = new Season
                    {
                        Name = seasonNameFallback,
                        SortName = sortNameFallback,
                        ForcedSortName = sortNameFallback,
                        OriginalTitle = seasonNameFallback,
                        IndexNumber = seasonNumber
                    };

                    result.Item = fallbackSeason;
                    result.HasMetadata = true;
                    return result;
                }

                _logger.LogWarning("No season detail found for {Name} S{SeasonNumber} (AniList base {AniListId})", info.Name, seasonNumber, baseAniListId);
                return result;
            }

            var seasonName = GetSeasonName(seasonDetail, seasonNumber, info.Name);
            var sortName = $"Season {seasonNumber:00}";
            var originalTitle = seasonDetail.TitleEnglish
                ?? seasonDetail.TitleRomaji
                ?? seasonDetail.TitleNative
                ?? seasonName;

            string? overview = null;
            int? malIdForOverview = seasonDetail.MalId;
            if (ShouldUseJikanOverview() && !malIdForOverview.HasValue)
            {
                malIdForOverview = await ResolveSeriesMalIdAsync(info);
            }

            if (ShouldUseJikanOverview() && malIdForOverview.HasValue)
            {
                var jikanData = await _apiService.GetJikanAnimeAsync(malIdForOverview.Value);
                overview = jikanData?.Synopsis;
            }

            if (string.IsNullOrWhiteSpace(overview))
            {
                overview = seasonDetail.Description;
            }

            var season = new Season
            {
                Name = seasonName,
                SortName = sortName,
                ForcedSortName = sortName,
                OriginalTitle = originalTitle,
                Overview = overview,
                IndexNumber = seasonNumber
            };

            if (seasonDetail.StartDate.HasValue)
            {
                season.PremiereDate = seasonDetail.StartDate.Value;
                season.ProductionYear = seasonDetail.StartDate.Value.Year;
            }

            if (seasonDetail.EndDate.HasValue)
            {
                // Season does not expose a dedicated end date field in some Jellyfin versions; ignore if not supported.
            }

            if (seasonDetail.AverageScore.HasValue)
            {
                season.CommunityRating = (float)(seasonDetail.AverageScore.Value / 10.0);
            }

            if (seasonDetail.Genres?.Count > 0)
            {
                var genres = seasonDetail.Genres;
                season.Genres = genres.ToArray();
            }

            season.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            season.SetProviderId(Constants.AniListProviderId, seasonDetail.AniListId.ToString());
            if (seasonDetail.MalId.HasValue)
            {
                season.SetProviderId(Constants.MalProviderId, seasonDetail.MalId.Value.ToString());
            }

            // Season-level tags from AniDB via mapping
            if (_config.EnableAniDbTags)
            {
                var seasonTags = await GetSeasonTagsAsync(seasonDetail.AniListId);
                season.Tags = seasonTags.ToArray();
            }

            result.Item = season;
            result.HasMetadata = true;
            _logger.LogInformation("Season metadata populated for {Name} S{SeasonNumber} with AniList ID {AniListId}", info.Name, seasonNumber, seasonDetail.AniListId);
            return result;
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        private async Task<int?> ResolveRootAniListIdAsync(SeasonInfo info)
        {
            // 1) Try provider IDs from series or season
            var idFromInfo = GetAniListIdFromInfo(info);
            if (idFromInfo.HasValue)
            {
                return await _apiService.GetRootAniListIdAsync(idFromInfo.Value);
            }

            // 2) Try reading parent .plexmatch and mapping via Fribb list
            var seasonPath = info.Path ?? string.Empty;
            var seriesDir = Directory.GetParent(seasonPath)?.FullName;
            if (string.IsNullOrEmpty(seriesDir))
            {
                _logger.LogDebug("No parent directory for season path {Path}", seasonPath);
                return null;
            }

            var plexMatchPath = Path.Combine(seriesDir, Constants.PlexMatchFileName);
            if (!File.Exists(plexMatchPath))
            {
                _logger.LogDebug(".plexmatch not found at {PlexMatchPath}", plexMatchPath);
                return null;
            }

            try
            {
                var content = await File.ReadAllTextAsync(plexMatchPath);
                var plexData = _plexMatchParser.ParsePlexMatch(content);

                await _animeListMapper.LoadAnimeListsAsync();
                AnimeMapping? mapping = null;
                if (!string.IsNullOrEmpty(plexData.TvdbId))
                {
                    mapping = _animeListMapper.GetMappingByTvdbId(plexData.TvdbId);
                }

                if (mapping == null && !string.IsNullOrEmpty(plexData.ImdbId))
                {
                    mapping = _animeListMapper.GetMappingByImdbId(plexData.ImdbId);
                }

                if (mapping?.anilist_id.HasValue == true)
                {
                    _logger.LogInformation("Resolved AniList ID {AniListId} for series '{Title}' via .plexmatch", mapping.anilist_id, plexData.Title);
                    return await _apiService.GetRootAniListIdAsync(mapping.anilist_id.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving AniList ID from .plexmatch for season path {Path}", seasonPath);
            }

            return null;
        }

        private int? GetAniListIdFromInfo(SeasonInfo info)
        {
            if (info.SeriesProviderIds != null &&
                TryGetValueIgnoreCase(info.SeriesProviderIds, Constants.AniListProviderId, out var seriesAniList) &&
                int.TryParse(seriesAniList, out var seriesId))
            {
                return seriesId;
            }

            if (info.ProviderIds != null &&
                TryGetValueIgnoreCase(info.ProviderIds, Constants.AniListProviderId, out var seasonAniList) &&
                int.TryParse(seasonAniList, out var seasonId))
            {
                return seasonId;
            }

            return null;
        }

        private bool TryGetValueIgnoreCase(IDictionary<string, string> dict, string key, out string value)
        {
            foreach (var kvp in dict)
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

        private string GetSeasonName(ApiService.AniListSeasonDetail seasonDetail, int seasonNumber, string fallbackName)
        {
            if (_config.SeasonTitleFormat == SeasonTitleFormatType.Numbered)
            {
                return seasonNumber <= 0 ? "Specials" : $"Season {seasonNumber}";
            }

            return seasonDetail.TitleEnglish
                ?? seasonDetail.TitleRomaji
                ?? seasonDetail.TitleNative
                ?? (seasonNumber == 0 ? "Specials" : fallbackName);
        }

        private bool ShouldUseJikanOverview()
        {
            return _config.SeasonOverviewSource == SeasonOverviewSourceType.Jikan
                || _config.SeasonOverviewSource == SeasonOverviewSourceType.PreferJikan;
        }

        private async Task<int?> ResolveSeriesMalIdAsync(SeasonInfo info)
        {
            if (info.SeriesProviderIds != null &&
                TryGetValueIgnoreCase(info.SeriesProviderIds, Constants.MalProviderId, out var seriesMal) &&
                int.TryParse(seriesMal, out var seriesMalId))
            {
                return seriesMalId;
            }

            // Try mapping via .plexmatch
            var seasonPath = info.Path ?? string.Empty;
            var seriesDir = Directory.GetParent(seasonPath)?.FullName;
            if (string.IsNullOrEmpty(seriesDir))
            {
                return null;
            }

            var plexMatchPath = Path.Combine(seriesDir, Constants.PlexMatchFileName);
            if (!File.Exists(plexMatchPath))
            {
                return null;
            }

            try
            {
                var content = await File.ReadAllTextAsync(plexMatchPath);
                var plexData = _plexMatchParser.ParsePlexMatch(content);

                await _animeListMapper.LoadAnimeListsAsync();
                AnimeMapping? mapping = null;
                if (!string.IsNullOrEmpty(plexData.TvdbId))
                {
                    mapping = _animeListMapper.GetMappingByTvdbId(plexData.TvdbId);
                }

                if (mapping == null && !string.IsNullOrEmpty(plexData.ImdbId))
                {
                    mapping = _animeListMapper.GetMappingByImdbId(plexData.ImdbId);
                }

                if (mapping?.mal_id.HasValue == true)
                {
                    return (int)mapping.mal_id.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving MAL ID from .plexmatch for season path {Path}", seasonPath);
            }

            return null;
        }

        private async Task<List<string>> GetSeasonTagsAsync(int aniListSeasonId)
        {
            try
            {
                await _animeListMapper.LoadAnimeListsAsync();
                var mapping = _animeListMapper.GetMappingByAniListId(aniListSeasonId);
                var aniDbId = mapping?.anidb_id;
                if (!aniDbId.HasValue)
                {
                    return new List<string>();
                }

                var tags = await _apiService.GetAniDbTagsAsync(
                    aniDbId.Value,
                    _config.AniDbClientName,
                    _config.AniDbClientVersion,
                    _config.AniDbRateLimit);

                if (tags == null || tags.Count == 0)
                {
                    return new List<string>();
                }

                var filtered = _tagFilterService.FilterTags(tags);
                _tagFilterService.LogFilteredTags(_logger, tags, filtered);
                return filtered;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching season tags for AniList season {AniListId}", aniListSeasonId);
                return new List<string>();
            }
        }
    }
}
