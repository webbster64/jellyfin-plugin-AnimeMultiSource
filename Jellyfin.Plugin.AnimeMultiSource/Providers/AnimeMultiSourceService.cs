using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading; // Add this for Interlocked
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities; // For PersonInfo
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;


namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class AnimeMetadata
    {
        // Basic info
        public string? Title { get; set; }
        public string? OriginalTitle { get; set; }
        public int? Year { get; set; }

        // Provider IDs
        public string? TvdbId { get; set; }
        public string? ImdbId { get; set; }
        public string? TheMovieDbId { get; set; }
        public string? AniDbId { get; set; }
        public string? AniListId { get; set; }
        public string? AniSearchId { get; set; }
        public string? KitsuId { get; set; }
        public string? MalId { get; set; }
        public string? Type { get; set; }
        public string? AnimePlanetId { get; set; }

        // New metadata fields
        public string? Status { get; set; }
        public double? CommunityRating { get; set; }
        public string? Overview { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? Runtime { get; set; }
        public string? ParentalRating { get; set; }
        public List<string>? Genres { get; set; }
        public List<string>? Studios { get; set; }
        public List<string>? Tags { get; set; }
        public List<PersonInfo>? People { get; set; }
        public List<SeasonRelation>? Seasons { get; set; }
    }

    public class SeasonRelation
    {
        public string RelationType { get; set; } = "UNKNOWN";
        public int AniListId { get; set; }
        public string? Title { get; set; }
        public string? TitleEnglish { get; set; }
        public string? Season { get; set; }
        public int? SeasonYear { get; set; }
        public string? Format { get; set; }
        public int? Episodes { get; set; }
    }

    public class AnimeMultiSourceService
    {
        private readonly ILogger _logger;
        private readonly PlexMatchParser _plexMatchParser;
        private readonly AnimeListMapper _animeListMapper;
        private readonly ApiService _apiService;
        private readonly TagFilterService _tagFilterService;

        public AnimeMultiSourceService(ILogger logger)
        {
            _logger = logger;
            _plexMatchParser = new PlexMatchParser(logger);
            _tagFilterService = new TagFilterService();
            
            // Create HttpClient with automatic decompression
            var httpClientHandler = new System.Net.Http.HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            
            var httpClient = new System.Net.Http.HttpClient(httpClientHandler);
            _animeListMapper = new AnimeListMapper(httpClient, logger);
            _apiService = new ApiService(httpClient, logger);
        }

        private static int _serviceCallCount = 0;

        public async Task<AnimeMetadata?> GetMetadataForSeries(SeriesInfo info, Configuration.PluginConfiguration config)
        {
            var callId = Interlocked.Increment(ref _serviceCallCount);
            _logger.LogInformation("=== SERVICE CALL #{CallId} for: {Name} ===", callId, info.Name);
            
            var seriesPath = info.Path;

            // Look for .plexmatch file
            var plexMatchPath = Path.Combine(seriesPath, Constants.PlexMatchFileName);
            if (!File.Exists(plexMatchPath))
            {
                _logger.LogWarning("***********************");
                _logger.LogWarning("* no plexmatch file  *");
                _logger.LogWarning("***********************");
                return null;
            }

            _logger.LogInformation("Found .plexmatch file at {PlexMatchPath}", plexMatchPath);

            // Parse .plexmatch file
            var plexMatchContent = await File.ReadAllTextAsync(plexMatchPath);
            var plexMatchData = _plexMatchParser.ParsePlexMatch(plexMatchContent);

            _logger.LogInformation("Parsed .plexmatch data - Title: {Title}, Year: {Year}, TVDB: {TvdbId}, IMDb: {ImdbId}",
                plexMatchData.Title, plexMatchData.Year, plexMatchData.TvdbId, plexMatchData.ImdbId);

            // Ensure anime lists are loaded
            await _animeListMapper.LoadAnimeListsAsync();

            // Find mapping
            AnimeMapping? mapping = null;
            if (!string.IsNullOrEmpty(plexMatchData.TvdbId))
            {
                mapping = _animeListMapper.GetMappingByTvdbId(plexMatchData.TvdbId);
            }

            if (mapping == null && !string.IsNullOrEmpty(plexMatchData.ImdbId))
            {
                mapping = _animeListMapper.GetMappingByImdbId(plexMatchData.ImdbId);
            }

            if (mapping == null)
            {
                _logger.LogWarning("No mapping found for series '{Title}'", plexMatchData.Title);
                return null;
            }

            _logger.LogInformation("Successfully mapped series '{Title}' to anime with AniDB: {AniDbId}, AniList: {AniListId}, MAL: {MalId}",
                plexMatchData.Title, mapping.anidb_id, mapping.anilist_id, mapping.mal_id);

            long? rootAniListId = mapping.anilist_id;
            AniListMedia? aniListData = null;
            JikanAnime? jikanData = null;
            long? effectiveMalId = mapping.mal_id;

            if (mapping.anilist_id.HasValue)
            {
                rootAniListId = await _apiService.GetRootAniListIdAsync(mapping.anilist_id.Value);
                if (rootAniListId != mapping.anilist_id)
                {
                    _logger.LogInformation("Using AniList root ID {RootId} instead of mapped ID {MappedId} for series '{Title}'", rootAniListId, mapping.anilist_id, plexMatchData.Title);

                    // Try to realign mapping using the root AniList ID so other provider IDs match the base series
                    var rootMapping = _animeListMapper.GetMappingByAniListId(rootAniListId.Value);
                    if (rootMapping != null)
                    {
                        _logger.LogInformation("Swapped mapping to AniList root {RootId} for series '{Title}' to correct provider IDs", rootAniListId, plexMatchData.Title);
                        mapping = rootMapping;
                    }
                    else
                    {
                        _logger.LogWarning("No mapping found for AniList root {RootId}; keeping original mapping for provider IDs", rootAniListId);
                    }
                }
            }

            // Fetch from AniList if we have AniList ID
            if (rootAniListId.HasValue)
            {
                aniListData = await _apiService.GetAniListAnimeAsync(rootAniListId.Value);
            }

            // Prefer MAL ID from AniList root if available (avoids OVA mappings)
            if (aniListData?.IdMal.HasValue == true)
            {
                effectiveMalId = aniListData.IdMal.Value;
            }

            // Fetch from Jikan using effective MAL ID
            if (effectiveMalId.HasValue)
            {
                jikanData = await _apiService.GetJikanAnimeAsync(effectiveMalId.Value);
            }

            _logger.LogInformation("=== SERVICE COMPLETED CALL #{CallId} ===", callId);

            // Create combined metadata
            return await CreateCombinedMetadata(plexMatchData, mapping, jikanData, aniListData, rootAniListId, effectiveMalId, config);
        }

        private async Task<AnimeMetadata> CreateCombinedMetadata(
            PlexMatchData plexMatchData, 
            AnimeMapping mapping,
            JikanAnime? jikanData,
            AniListMedia? aniListData,
            long? rootAniListId,
            long? effectiveMalId,
            Configuration.PluginConfiguration config)
        {
            var metadata = new AnimeMetadata
            {
                // Basic info from .plexmatch
                Title = plexMatchData.Title,
                Year = plexMatchData.Year,

                // Provider IDs from .plexmatch
                TvdbId = plexMatchData.TvdbId,
                ImdbId = plexMatchData.ImdbId,

                // Provider IDs from mapping
                AniDbId = mapping.anidb_id?.ToString(),
                AniListId = rootAniListId?.ToString(),
                AniSearchId = mapping.anisearch_id?.ToString(),
                KitsuId = mapping.kitsu_id?.ToString(),
                MalId = effectiveMalId?.ToString(),
                TheMovieDbId = mapping.themoviedb_id?.ToString(),
                Type = mapping.type,
                AnimePlanetId = mapping.animeplanet_id
            };

            // Avoid using Jikan when it points to a non-TV entry (e.g., OVA mapped by mistake)
            var primaryJikan = ShouldUseJikanForSeries(jikanData) ? jikanData : null;
            if (jikanData != null && primaryJikan == null)
            {
                _logger.LogWarning("Ignoring Jikan metadata for {Title} because type '{Type}' is not TV", metadata.Title, jikanData.Type);
            }

            // Apply configuration for Title and OriginalTitle selection
            metadata.Title = GetConfiguredTitle(primaryJikan, aniListData, config);
            metadata.OriginalTitle = GetConfiguredOriginalTitle(primaryJikan, aniListData, config);

            // Status from Jikan
            metadata.Status = _apiService.MapJikanStatus(primaryJikan?.Status);

            // Community Rating - convert AniList score (0-100) to 0-10 scale
            metadata.CommunityRating = primaryJikan?.Score ?? (aniListData?.AverageScore / 10.0);

            // Overview - prefer Jikan, fallback to AniList
            metadata.Overview = primaryJikan?.Synopsis ?? aniListData?.Description;

            // Dates from Jikan
            metadata.ReleaseDate = primaryJikan?.Aired?.From;
            metadata.EndDate = primaryJikan?.Aired?.To;

            // Runtime based on configuration
            metadata.Runtime = GetConfiguredRuntime(primaryJikan, aniListData, config);

            // Parental Rating from Jikan
            metadata.ParentalRating = primaryJikan?.Rating;

            // Genres - combined from Jikan and AniList, filtered by approved list
            var approvedGenres = GetApprovedGenres(config);
            metadata.Genres = _apiService.CombineGenres(primaryJikan, aniListData, approvedGenres);

            // Studios from Jikan
            metadata.Studios = _apiService.GetStudios(jikanData);

            // Fetch People data asynchronously
            if (rootAniListId.HasValue || mapping.mal_id.HasValue)
            {
                try
                {
                    metadata.People = await _apiService.GetPeopleAsync(
                        rootAniListId ?? 0, 
                        mapping.mal_id ?? 0
                    );
                    _logger.LogDebug("Fetched {Count} people for {Title}", metadata.People?.Count ?? 0, metadata.Title);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching people data for {Title}", metadata.Title);
                    metadata.People = new List<PersonInfo>();
                }
            }
            else
            {
                metadata.People = new List<PersonInfo>();
            }

            // Fetch season relations from AniList if available
            if (aniListData != null)
            {
                metadata.Seasons = _apiService.GetSequelRelations(aniListData);
                if (metadata.Seasons?.Count > 0)
                {
                    _logger.LogInformation("Found {Count} AniList season relations for {Title}", metadata.Seasons.Count, metadata.Title);
                }
            }

            // Fetch tags from AniDB if enabled and we have an AniDB ID
            if (config.EnableAniDbTags && !string.IsNullOrEmpty(metadata.AniDbId))
            {
                if (long.TryParse(metadata.AniDbId, out long anidbId))
                {
                    _logger.LogDebug("Attempting to fetch AniDB tags for ID: {AniDbId}", anidbId);
                    try
                    {
                        var aniDbTags = await _apiService.GetAniDbTagsAsync(
                            anidbId, 
                            config.AniDbClientName, 
                            config.AniDbClientVersion,
                            config.AniDbRateLimit
                        );
                        
                        metadata.Tags = aniDbTags;
                        _logger.LogDebug("Fetched {Count} AniDB tags for {Title}", aniDbTags.Count, metadata.Title);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error fetching AniDB tags for {Title}", metadata.Title);
                        metadata.Tags = new List<string>();
                    }
                }
                else
                {
                    _logger.LogWarning("Unable to parse AniDB ID: {AniDbId}", metadata.AniDbId);
                    metadata.Tags = new List<string>();
                }
            }
            else
            {
                metadata.Tags = new List<string>();
            }

            _logger.LogInformation("Successfully populated metadata for {Title} with {GenreCount} genres, {StudioCount} studios, {PeopleCount} people, {TagCount} tags",
                metadata.Title, metadata.Genres?.Count ?? 0, metadata.Studios?.Count ?? 0, metadata.People?.Count ?? 0, metadata.Tags?.Count ?? 0);

            return metadata;
        }

        // ... rest of your existing helper methods (GetConfiguredTitle, GetConfiguredOriginalTitle, etc.) remain the same ...
        private string GetConfiguredTitle(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.PluginConfiguration config)
        {
            // If we have configuration for title, use it
            var title = GetTitleFromSource(jikanData, aniListData, config.TitleField, config.TitleDataSource);
            return title ?? jikanData?.Title ?? aniListData?.Title?.Romaji ?? "Unknown Title";
        }

        private string GetConfiguredOriginalTitle(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.PluginConfiguration config)
        {
            // If we have configuration for original title, use it
            var originalTitle = GetOriginalTitleFromSource(jikanData, aniListData, config.OriginalTitleField, config.OriginalTitleDataSource);
            return originalTitle ?? jikanData?.TitleJapanese ?? aniListData?.Title?.Native ?? string.Empty;
        }

        private int? GetConfiguredRuntime(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.PluginConfiguration config)
        {
            if (config.RuntimeDataSource == Configuration.RuntimeDataSourceType.Anilist)
            {
                // Prefer AniList duration
                if (aniListData?.Duration.HasValue == true)
                    return aniListData.Duration;
            }

            // Fallback to Jikan
            var runtimeStr = _apiService.ParseJikanDuration(jikanData?.Duration);
            if (int.TryParse(runtimeStr, out int runtime))
            {
                return runtime;
            }

            // If preferred source failed, try the other
            if (config.RuntimeDataSource == Configuration.RuntimeDataSourceType.Jikan)
            {
                // We already tried Jikan, return null
                return null;
            }
            else
            {
                // Try Jikan as fallback
                runtimeStr = _apiService.ParseJikanDuration(jikanData?.Duration);
                if (int.TryParse(runtimeStr, out runtime))
                {
                    return runtime;
                }
            }

            return null;
        }

        private List<string> GetApprovedGenres(Configuration.PluginConfiguration config)
        {
            if (string.IsNullOrEmpty(config.ApprovedGenres))
                return new List<string>();

            return config.ApprovedGenres
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim())
                .Where(g => !string.IsNullOrEmpty(g))
                .ToList();
        }

        private string? GetTitleFromSource(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.TitleFieldType field, Configuration.DataSourceType source)
        {
            return source switch
            {
                Configuration.DataSourceType.Anilist => GetTitleFromAniList(aniListData, field),
                Configuration.DataSourceType.Jikan => GetTitleFromJikan(jikanData, field),
                _ => GetTitleFromJikan(jikanData, field) ?? GetTitleFromAniList(aniListData, field)
            };
        }

        private string? GetOriginalTitleFromSource(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.OriginalTitleFieldType field, Configuration.DataSourceType source)
        {
            return source switch
            {
                Configuration.DataSourceType.Anilist => GetOriginalTitleFromAniList(aniListData, field),
                Configuration.DataSourceType.Jikan => GetOriginalTitleFromJikan(jikanData, field),
                _ => GetOriginalTitleFromJikan(jikanData, field) ?? GetOriginalTitleFromAniList(aniListData, field)
            };
        }

        private string? GetTitleFromJikan(JikanAnime? jikanData, Configuration.TitleFieldType field)
        {
            if (jikanData == null) return null;

            return field switch
            {
                Configuration.TitleFieldType.Title => jikanData.Title,
                Configuration.TitleFieldType.TitleEnglish => jikanData.TitleEnglish,
                Configuration.TitleFieldType.TitleJapanese => jikanData.TitleJapanese,
                _ => jikanData.Title
            };
        }

        private string? GetTitleFromAniList(AniListMedia? aniListData, Configuration.TitleFieldType field)
        {
            if (aniListData?.Title == null) return null;

            return field switch
            {
                Configuration.TitleFieldType.Title => aniListData.Title.Romaji,
                Configuration.TitleFieldType.TitleEnglish => aniListData.Title.English,
                Configuration.TitleFieldType.TitleJapanese => aniListData.Title.Native,
                _ => aniListData.Title.Romaji
            };
        }

        private bool ShouldUseJikanForSeries(JikanAnime? jikanData)
        {
            if (jikanData == null) return false;
            if (string.IsNullOrWhiteSpace(jikanData.Type)) return true;

            return jikanData.Type.StartsWith("TV", StringComparison.OrdinalIgnoreCase);
        }

        private string? GetOriginalTitleFromJikan(JikanAnime? jikanData, Configuration.OriginalTitleFieldType field)
        {
            if (jikanData == null) return null;

            return field switch
            {
                Configuration.OriginalTitleFieldType.Title => jikanData.Title,
                Configuration.OriginalTitleFieldType.TitleJapanese => jikanData.TitleJapanese,
                _ => jikanData.TitleJapanese
            };
        }

        private string? GetOriginalTitleFromAniList(AniListMedia? aniListData, Configuration.OriginalTitleFieldType field)
        {
            if (aniListData?.Title == null) return null;

            return field switch
            {
                Configuration.OriginalTitleFieldType.Title => aniListData.Title.Romaji,
                Configuration.OriginalTitleFieldType.TitleJapanese => aniListData.Title.Native,
                _ => aniListData.Title.Native
            };
        }
    }
}
