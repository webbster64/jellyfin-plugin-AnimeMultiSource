using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>
    /// Metadata for anime movies. Meant to be the only metadata downloader of an "Anime Movies" library
    /// (turn TheMovieDb/OMDb off there) so two providers do not fight over the same fields.
    /// </summary>
    public class AnimeMultiSourceMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>
    {
        private readonly ILogger<AnimeMultiSourceMovieProvider> _logger;
        private readonly AnimeMultiSourceService _animeService;

        public AnimeMultiSourceMovieProvider(ILogger<AnimeMultiSourceMovieProvider> logger)
        {
            _logger = logger;
            _animeService = new AnimeMultiSourceService(logger);
        }

        public string Name => Constants.PluginName;

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Movie>();
            var config = Plugin.GetConfigurationSafe(_logger);
            if (ItemExclusion.IsExcluded(info.Path, config))
            {
                return result;
            }

            _logger.LogInformation("=== MOVIE PROVIDER for: {Name} ({Year}) Path: {Path} Ids: {Ids} ===", info.Name, info.Year, info.Path,
                info.ProviderIds == null ? "none" : string.Join(", ", info.ProviderIds.Select(p => $"{p.Key}={p.Value}")));

            try
            {
                var metadata = await _animeService.GetMetadataForMovie(info, config, cancellationToken);
                if (metadata == null)
                {
                    return result;
                }

                var movie = new Movie
                {
                    Name = metadata.Title,
                    OriginalTitle = metadata.OriginalTitle,
                    Overview = metadata.Overview,
                    OfficialRating = metadata.ParentalRating
                };

                if (metadata.Year.HasValue)
                {
                    movie.ProductionYear = metadata.Year.Value;
                }

                if (metadata.ReleaseDate.HasValue)
                {
                    movie.PremiereDate = metadata.ReleaseDate.Value;
                }

                if (metadata.CommunityRating.HasValue)
                {
                    movie.CommunityRating = (float)metadata.CommunityRating.Value;
                }

                if (metadata.Runtime.HasValue)
                {
                    movie.RunTimeTicks = TimeSpan.FromMinutes(metadata.Runtime.Value).Ticks;
                }

                movie.Genres = metadata.Genres?.ToArray() ?? Array.Empty<string>();
                movie.Studios = metadata.Studios?.ToArray() ?? Array.Empty<string>();
                movie.Tags = AnimeMultiSourceProvider.WithBadge(metadata.Tags, config);

                var tagCount = metadata.Tags?.Count ?? 0;
                if (tagCount > 0)
                {
                    movie.SetProviderId("AMSScore", tagCount.ToString());
                }

                SetProviderIds(movie, metadata);

                if (metadata.People != null)
                {
                    result.People = metadata.People;
                }

                result.Item = movie;
                result.HasMetadata = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting movie metadata for {Name}", info.Name);
            }

            return result;
        }

        private static void SetProviderIds(Movie movie, AnimeMetadata metadata)
        {
            void Set(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    movie.SetProviderId(key, value);
                }
            }

            Set(MetadataProvider.Tmdb.ToString(), metadata.TheMovieDbId);
            Set(MetadataProvider.Imdb.ToString(), metadata.ImdbId);
            Set(Constants.AniDbProviderId, metadata.AniDbId);
            Set(Constants.AniListProviderId, metadata.AniListId);
            Set(Constants.AniSearchProviderId, metadata.AniSearchId);
            Set(Constants.KitsuProviderId, metadata.KitsuId);
            Set(Constants.MalProviderId, metadata.MalId);
        }

        /// <summary>Feeds the "Identify" dialog; choosing a result stores its AniList/MAL id, which the next refresh then honours.</summary>
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            var config = Plugin.GetConfigurationSafe(_logger);
            var (title, year) = MovieIdentifier.ParseNameAndYear(searchInfo.Name, searchInfo.Year);
            if (string.IsNullOrWhiteSpace(title))
            {
                return Enumerable.Empty<RemoteSearchResult>();
            }

            try
            {
                var ranked = await _animeService.MovieIdentifier.SearchAsync(title, year, config, cancellationToken);
                return ranked.Take(10).Select(scored =>
                {
                    var candidate = scored.Candidate;
                    var result = new RemoteSearchResult
                    {
                        Name = candidate.English ?? candidate.Title ?? candidate.Romaji,
                        ProductionYear = candidate.Year,
                        SearchProviderName = Name
                    };

                    if (candidate.AniListId.HasValue)
                    {
                        result.SetProviderId(Constants.AniListProviderId, candidate.AniListId.Value.ToString());
                    }

                    if (candidate.MalId.HasValue)
                    {
                        result.SetProviderId(Constants.MalProviderId, candidate.MalId.Value.ToString());
                    }

                    return result;
                }).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Movie search failed for '{Title}'", title);
                return Enumerable.Empty<RemoteSearchResult>();
            }
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
