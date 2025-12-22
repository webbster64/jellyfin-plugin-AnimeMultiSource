using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;


namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class AnimeMultiSourceProvider : IRemoteMetadataProvider<Series, SeriesInfo>
    {
        private readonly ILogger<AnimeMultiSourceProvider> _logger;
        private readonly AnimeMultiSourceService _animeService;
        private readonly Configuration.PluginConfiguration _config;

        // Add a static counter to track how many times the provider is called
        private static int _callCount = 0;

        public AnimeMultiSourceProvider(ILogger<AnimeMultiSourceProvider> logger)
        {
            _logger = logger;
            _config = Plugin.GetConfigurationSafe(_logger);

            // Create service with simplified dependency chain
            _animeService = new AnimeMultiSourceService(logger);
        }

        public string Name => Constants.PluginName;

        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            var callId = Interlocked.Increment(ref _callCount);
            _logger.LogInformation("=== METADATA PROVIDER CALL #{CallId} for: {Name} (Path: {Path}) ===",
                callId, info.Name, info.Path);

            // Log the SeriesInfo properties to understand what's different between calls
            _logger.LogDebug("SeriesInfo details - Name: {Name}, Path: {Path}, Year: {Year}, IsAutomated: {IsAutomated}",
                info.Name, info.Path, info.Year, info.IsAutomated);

            var result = new MetadataResult<Series>();

            try
            {
                var metadata = await _animeService.GetMetadataForSeries(info, _config);
                if (metadata == null)
                {
                    _logger.LogDebug("No metadata found for {Path}", info.Path);
                    return result;
                }

                result.Item = new Series();
                result.HasMetadata = true;

                // Set basic info
                result.Item.Name = metadata.Title;
                result.Item.OriginalTitle = metadata.OriginalTitle;
                if (metadata.Year.HasValue)
                    result.Item.ProductionYear = metadata.Year.Value;

                // Set the new metadata fields
                if (!string.IsNullOrEmpty(metadata.Status))
                    result.Item.Status = GetSeriesStatus(metadata.Status);

                if (metadata.CommunityRating.HasValue)
                    result.Item.CommunityRating = (float)metadata.CommunityRating.Value;

                result.Item.Overview = metadata.Overview;

                if (metadata.ReleaseDate.HasValue)
                    result.Item.PremiereDate = metadata.ReleaseDate.Value;

                if (metadata.EndDate.HasValue)
                    result.Item.EndDate = metadata.EndDate.Value;

                if (metadata.Runtime.HasValue)
                    result.Item.RunTimeTicks = TimeSpan.FromMinutes(metadata.Runtime.Value).Ticks;

                result.Item.OfficialRating = metadata.ParentalRating;

                // Append AMS score (tag count) to rating display without altering the numeric rating.
                var amsScore = metadata.Tags?.Count ?? 0;
                if (amsScore > 0)
                {
                    result.Item.OfficialRating = string.IsNullOrWhiteSpace(result.Item.OfficialRating)
                        ? $"AMS-{amsScore}"
                        : $"{result.Item.OfficialRating} AMS-{amsScore}";
                }

                // Set genres
                if (metadata.Genres != null)
                    result.Item.Genres = metadata.Genres.ToArray();

                // Set studios
                if (metadata.Studios != null)
                    result.Item.Studios = metadata.Studios.ToArray();

                // Set tags
                if (metadata.Tags != null)
                    result.Item.Tags = metadata.Tags.ToArray();

                // Set people
                if (metadata.People != null)
                {
                    result.People = metadata.People;
                    _logger.LogInformation("Added {PeopleCount} people to metadata", metadata.People.Count);
                }

                // Set ALL provider IDs for External IDs section
                SetAllProviderIds(result.Item, metadata);

                _logger.LogInformation("Successfully set metadata for {Title} with {GenreCount} genres, {StudioCount} studios",
                    metadata.Title, metadata.Genres?.Count ?? 0, metadata.Studios?.Count ?? 0);

                _logger.LogInformation("=== METADATA PROVIDER COMPLETED CALL #{CallId} ===", callId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in call #{CallId} for {Name}", callId, info.Name);
            }

            return result;
        }

        private SeriesStatus GetSeriesStatus(string status)
        {
            return status?.ToLower() switch
            {
                "continuing" => SeriesStatus.Continuing,
                "ended" => SeriesStatus.Ended,
                "not yet released" => SeriesStatus.Unreleased,
                _ => SeriesStatus.Unreleased
            };
        }

        private void SetAllProviderIds(Series series, AnimeMetadata metadata)
        {
            // Clear existing provider IDs first
            series.ProviderIds ??= new Dictionary<string, string>();

            // Standard Jellyfin provider IDs
            if (!string.IsNullOrEmpty(metadata.TvdbId))
            {
                series.SetProviderId(MetadataProvider.Tvdb, metadata.TvdbId);
                _logger.LogDebug("Set TvdbId: {TvdbId}", metadata.TvdbId);
            }

            if (!string.IsNullOrEmpty(metadata.ImdbId))
            {
                series.SetProviderId(MetadataProvider.Imdb, metadata.ImdbId);
                _logger.LogDebug("Set ImdbId: {ImdbId}", metadata.ImdbId);
            }

            if (!string.IsNullOrEmpty(metadata.TheMovieDbId))
            {
                series.SetProviderId(MetadataProvider.Tmdb, metadata.TheMovieDbId);
                _logger.LogDebug("Set TmdbId: {TmdbId}", metadata.TheMovieDbId);
            }

            // Anime-specific provider IDs - Use the EXACT same IDs as other plugins
            if (!string.IsNullOrEmpty(metadata.AniDbId))
            {
                series.SetProviderId("AniDB", metadata.AniDbId);
                _logger.LogDebug("Set AniDB Id: {AniDbId}", metadata.AniDbId);
            }

            if (!string.IsNullOrEmpty(metadata.AniListId))
            {
                series.SetProviderId("AniList", metadata.AniListId);
                _logger.LogDebug("Set AniList Id: {AniListId}", metadata.AniListId);
            }

            if (!string.IsNullOrEmpty(metadata.AniSearchId))
            {
                series.SetProviderId("AniSearch", metadata.AniSearchId);
                _logger.LogDebug("Set AniSearch Id: {AniSearchId}", metadata.AniSearchId);
            }

            if (!string.IsNullOrEmpty(metadata.KitsuId))
            {
                series.SetProviderId("Kitsu", metadata.KitsuId);
                _logger.LogDebug("Set Kitsu Id: {KitsuId}", metadata.KitsuId);
            }

            if (!string.IsNullOrEmpty(metadata.MalId))
            {
                series.SetProviderId("Mal", metadata.MalId);
                _logger.LogDebug("Set Mal Id: {MalId}", metadata.MalId);
            }

            // Log all set provider IDs
            _logger.LogInformation("Set {Count} provider IDs for {Title}: {ProviderIds}",
                series.ProviderIds.Count, metadata.Title, string.Join(", ", series.ProviderIds.Keys));
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
