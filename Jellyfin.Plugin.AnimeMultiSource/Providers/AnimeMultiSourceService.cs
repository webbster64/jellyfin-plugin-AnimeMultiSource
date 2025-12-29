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
        private static readonly HashSet<long> _keepAniListMappingIds = new()
        {
            // Known cases where the sequel/spin-off must not be realigned to a different root
            153452, // Ranking of Kings: The Treasure Chest of Courage
            223,    // Dragon Ball
            225,    // Dragon Ball GT
            813,    // Dragon Ball Z
            6033,   // Dragon Ball Z Kai
            21175,  // Dragon Ball Super
            170083  // Dragon Ball Daima
            // Add more AniList IDs here when you find edge cases (e.g., specific Dragon Ball entries)
        };
        // Long-episode “season” exceptions (movies that should still count as season bridges)
        private static readonly HashSet<long> _longEpisodeAniListIds = new()
        {
            187 // Initial D Third Stage (movie, ~104 minutes)
        };
        private static readonly HashSet<long> _deferSeasonAniListIds = new()
        {
            // Defer all seasons to TVDB/other providers for the Dragon Ball family
            223,    // Dragon Ball
            225,    // Dragon Ball GT
            813,    // Dragon Ball Z
            6033,   // Dragon Ball Z Kai
            21175,  // Dragon Ball Super
            170083  // Dragon Ball Daima
        };

        public static bool ShouldDeferSeasonToTvdb(long baseAniListId, int seasonNumber)
        {
            // Defer all seasons for specified AniList IDs to avoid bad AniList chains/names.
            return _deferSeasonAniListIds.Contains(baseAniListId);
        }

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
                var mappedAniListIdValue = mapping.anilist_id!.Value;
                aniListData = await _apiService.GetAniListAnimeAsync(mappedAniListIdValue);

                var keepMapping = _keepAniListMappingIds.Contains(mappedAniListIdValue);

                // If the mapped AniList entry is a sequel (e.g., Initial D Fourth Stage),
                // try walking PREQUEL links to find the closest year match for the plexmatch year.
                if (!keepMapping && plexMatchData.Year.HasValue && aniListData?.Relations?.Edges != null)
                {
                    var prequelRoot = await FindBestPrequelRootAsync(mappedAniListIdValue, plexMatchData.Year!.Value);
                    if (prequelRoot.HasValue && prequelRoot.Value != mappedAniListIdValue)
                    {
                        _logger.LogInformation("Switching to AniList prequel {PrequelId} for '{Title}' based on year match to {Year}",
                            prequelRoot, plexMatchData.Title, plexMatchData.Year);
                        rootAniListId = prequelRoot;
                        aniListData = await _apiService.GetAniListAnimeAsync(prequelRoot.Value);

                        var prequelMapping = _animeListMapper.GetMappingByAniListId(prequelRoot.Value);
                        if (prequelMapping != null && prequelMapping.thetvdb_id.HasValue && mapping.thetvdb_id.HasValue &&
                            prequelMapping.thetvdb_id.Value == mapping.thetvdb_id.Value)
                        {
                            mapping = prequelMapping;
                        }
                    }
                }

                var proposedRoot = keepMapping
                    ? (rootAniListId ?? mapping.anilist_id)
                    : await _apiService.GetRootAniListIdAsync(rootAniListId ?? mappedAniListIdValue);

                if (!keepMapping && proposedRoot != mapping.anilist_id)
                {
                    _logger.LogInformation("Using AniList root ID {RootId} instead of mapped ID {MappedId} for series '{Title}'", proposedRoot, mappedAniListIdValue, plexMatchData.Title);
                }

                // Choose between mapped vs root based on year proximity when available
                if (proposedRoot.HasValue && proposedRoot != mappedAniListIdValue && !keepMapping)
                {
                    var mappedMedia = await _apiService.GetAniListAnimeAsync(mappedAniListIdValue);
                    var rootMedia = await _apiService.GetAniListAnimeAsync(proposedRoot.Value);

                    int? plexYear = plexMatchData.Year;
                    int? mappedYear = mappedMedia?.StartDate?.Year;
                    int? rootYear = rootMedia?.StartDate?.Year;

                    bool haveYear = plexYear.HasValue && (mappedYear.HasValue || rootYear.HasValue);
                    if (haveYear)
                    {
                        var targetYear = plexYear.GetValueOrDefault();
                        var mappedDelta = mappedYear.HasValue ? Math.Abs(mappedYear.Value - targetYear) : int.MaxValue;
                        var rootDelta = rootYear.HasValue ? Math.Abs(rootYear.Value - targetYear) : int.MaxValue;

                        if (mappedDelta <= rootDelta)
                        {
                            _logger.LogInformation("Keeping mapped AniList ID {MappedId} for '{Title}' based on year proximity (mapped {MappedYear}, root {RootYear}, target {TargetYear})",
                                mapping.anilist_id, plexMatchData.Title, mappedYear, rootYear, plexYear);
                            rootAniListId = mapping.anilist_id;
                            aniListData = mappedMedia;
                        }
                        else
                        {
                            _logger.LogInformation("Switching to AniList root {RootId} for '{Title}' based on year proximity (mapped {MappedYear}, root {RootYear}, target {TargetYear})",
                                proposedRoot, plexMatchData.Title, mappedYear, rootYear, plexYear);
                            rootAniListId = proposedRoot;
                            aniListData = rootMedia;
                        }
                    }
                    else
                    {
                        rootAniListId = proposedRoot;
                    }

                    // Try to realign mapping using the chosen AniList ID if TVDB matches
                    if (rootAniListId.HasValue && rootAniListId != mapping.anilist_id)
                    {
                        var rootMapping = _animeListMapper.GetMappingByAniListId(rootAniListId.Value);
                        if (rootMapping != null && rootMapping.thetvdb_id.HasValue && mapping.thetvdb_id.HasValue &&
                            rootMapping.thetvdb_id.Value == mapping.thetvdb_id.Value)
                        {
                            _logger.LogInformation("Swapped mapping to AniList {ChosenId} for series '{Title}' to correct provider IDs", rootAniListId, plexMatchData.Title);
                            mapping = rootMapping;
                        }
                        else
                        {
                            _logger.LogWarning("Keeping original mapping for '{Title}' (chosen {ChosenId}) to avoid mismatching provider IDs", plexMatchData.Title, rootAniListId);
                        }
                    }
                }
                else
                {
                    rootAniListId = mapping.anilist_id;
                }
            }

            // Fetch from AniList if we have AniList ID
            if (rootAniListId.HasValue)
            {
                aniListData ??= await _apiService.GetAniListAnimeAsync(rootAniListId.Value);
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
            // Prefer root AniList media when mapping points at a sequel/spin-off stage.
            AniListMedia? aniListPrimary = aniListData;
            var mappedAniListId = mapping.anilist_id;
            var mappedIsPinned = mappedAniListId.HasValue && _keepAniListMappingIds.Contains(mappedAniListId.Value);
            if (rootAniListId.HasValue && aniListData?.Id != rootAniListId.Value && !mappedIsPinned)
            {
                var rootMedia = await _apiService.GetAniListAnimeAsync(rootAniListId.Value);
                if (rootMedia != null)
                {
                    var plexYear = plexMatchData.Year;
                    var mappedYear = aniListData?.StartDate?.Year;
                    var rootYear = rootMedia.StartDate?.Year;

                    var useRoot = true;
                    if (plexYear.HasValue && mappedYear.HasValue && rootYear.HasValue)
                    {
                        var targetYear = plexYear.Value;
                        var diffMapped = Math.Abs(mappedYear.Value - targetYear);
                        var diffRoot = Math.Abs(rootYear.Value - targetYear);
                        useRoot = diffRoot <= diffMapped;
                    }

                    if (useRoot)
                    {
                        aniListPrimary = rootMedia;
                    }
                }
            }

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
            metadata.Title = GetConfiguredTitle(primaryJikan, aniListPrimary, config);
            metadata.OriginalTitle = GetConfiguredOriginalTitle(primaryJikan, aniListPrimary, config);

            // Status from Jikan
            metadata.Status = _apiService.MapJikanStatus(primaryJikan?.Status);

            // Community Rating - convert AniList score (0-100) to 0-10 scale
            metadata.CommunityRating = primaryJikan?.Score ?? (aniListPrimary?.AverageScore / 10.0);

            // Overview - prefer Jikan, fallback to AniList
            metadata.Overview = primaryJikan?.Synopsis ?? aniListPrimary?.Description;

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
            if (config.EnableAniDbTags)
            {
                metadata.Tags = await GetAggregatedAniDbTagsAsync(mapping, metadata.Seasons, config);
            }
            else
            {
                metadata.Tags = new List<string>();
            }

            _logger.LogInformation("Successfully populated metadata for {Title} with {GenreCount} genres, {StudioCount} studios, {PeopleCount} people, {TagCount} tags",
                metadata.Title, metadata.Genres?.Count ?? 0, metadata.Studios?.Count ?? 0, metadata.People?.Count ?? 0, metadata.Tags?.Count ?? 0);

            return metadata;
        }

        private async Task<List<string>> GetAggregatedAniDbTagsAsync(
            AnimeMapping mapping,
            List<SeasonRelation>? seasons,
            Configuration.PluginConfiguration config)
        {
            var tagBuckets = new List<string>();
            var seenIds = new HashSet<long>();

            await _animeListMapper.LoadAnimeListsAsync();

            void AddId(long? id)
            {
                if (id.HasValue && id.Value > 0)
                {
                    seenIds.Add(id.Value);
                }
            }

            AddId(mapping.anidb_id);

            if (seasons != null)
            {
                foreach (var season in seasons)
                {
                    var seasonMapping = _animeListMapper.GetMappingByAniListId(season.AniListId);
                    AddId(seasonMapping?.anidb_id);
                }
            }

            foreach (var id in seenIds)
            {
                try
                {
                    _logger.LogDebug("Attempting to fetch AniDB tags for aggregated ID: {AniDbId}", id);
                    var tags = await _apiService.GetAniDbTagsAsync(
                        id,
                        config.AniDbClientName,
                        config.AniDbClientVersion,
                        config.AniDbRateLimit);
                    if (tags?.Count > 0)
                    {
                        tagBuckets.AddRange(tags);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching AniDB tags for aggregated ID {AniDbId}", id);
                }
            }

            var filtered = _tagFilterService.FilterTags(tagBuckets);
            _tagFilterService.LogFilteredTags(_logger, tagBuckets, filtered);
            return filtered;
        }

        private async Task<long?> FindBestPrequelRootAsync(long startAniListId, int targetYear)
        {
            var visited = new HashSet<long>();
            var queue = new Queue<long>();
            queue.Enqueue(startAniListId);

            long? bestId = startAniListId;
            int bestScore = int.MaxValue;

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                if (!visited.Add(currentId))
                {
                    continue;
                }

                var media = await _apiService.GetAniListAnimeAsync(currentId);
                if (media == null)
                {
                    continue;
                }

                int score = ScoreAniListMedia(media, targetYear);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestId = media.Id;
                }

                var prequels = media.Relations?.Edges?
                    .Where(e => string.Equals(e.RelationType, "PREQUEL", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Node?.Id)
                    .Where(id => id.HasValue)
                    .Select(id => (long)id!.Value) ?? Enumerable.Empty<long>();

                foreach (var prequelId in prequels)
                {
                    if (!visited.Contains(prequelId))
                    {
                        queue.Enqueue(prequelId);
                    }
                }
            }

            return bestId;
        }

        private int ScoreAniListMedia(AniListMedia media, int targetYear)
        {
            int year = media.StartDate?.Year ?? media.Relations?.Edges?
                .Select(e => e.Node?.SeasonYear)
                .FirstOrDefault(y => y.HasValue) ?? 0;

            int yearDelta = year > 0 ? Math.Abs(year - targetYear) : 50;

            var format = media.Format ?? string.Empty;
            bool isMovie = format.Equals("MOVIE", StringComparison.OrdinalIgnoreCase)
                           || (media.Duration.HasValue && media.Duration.Value > 60 && !_longEpisodeAniListIds.Contains(media.Id));

            int formatPenalty = isMovie ? 5 : 0;
            return yearDelta * 10 + formatPenalty;
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

            return jikanData.Type.StartsWith("TV", StringComparison.OrdinalIgnoreCase)
                || jikanData.Type.Equals("ONA", StringComparison.OrdinalIgnoreCase);
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
