using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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
        private readonly System.Net.Http.HttpClient _httpClient;
        private readonly MovieIdentifier _movieIdentifier;
        private readonly SeriesIdentifier _seriesIdentifier;
        private readonly EntryDetailsLoader _entryDetails;
        private readonly AnimeListsApiClient _animeListsApiClient;
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
        private static readonly List<AnimeMapping> _manualMappings = new()
        {
            // Fribb missing TVDB/IMDb: True Beauty (2024)
            new AnimeMapping
            {
                tvdb_id = 442363,
                imdb_id = ["tt33054444"],
                anilist_id = 152184,
                mal_id = 57192,
                type = "ONA"
            }
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
            _httpClient = httpClient;
            _animeListMapper = new AnimeListMapper(httpClient, logger);
            _apiService = new ApiService(httpClient, logger);
            _animeListsApiClient = new AnimeListsApiClient(httpClient, logger);
            _movieIdentifier = new MovieIdentifier(logger, _apiService, _animeListsApiClient, _plexMatchParser);
            _seriesIdentifier = new SeriesIdentifier(logger, _apiService, _animeListsApiClient);
            _entryDetails = new EntryDetailsLoader(_apiService, logger);
        }

        public MovieIdentifier MovieIdentifier => _movieIdentifier;

        public SeriesIdentifier SeriesIdentifier => _seriesIdentifier;

        /// <summary>
        /// Identifies an anime movie (see <see cref="MovieIdentifier"/>) and builds its metadata from AniList, Jikan and
        /// AniDB. Returns null when the movie cannot be identified with confidence.
        /// </summary>
        public async Task<AnimeMetadata?> GetMetadataForMovie(MovieInfo info, Configuration.PluginConfiguration config, CancellationToken cancellationToken)
        {
            var match = await _movieIdentifier.IdentifyAsync(info, config, cancellationToken);
            if (match == null)
            {
                return null;
            }

            AniListMovieMedia? aniList = match.AniListId.HasValue
                ? await _apiService.GetAniListMovieMediaAsync(match.AniListId.Value)
                : null;

            // Films that AniList lists as one combined entry (e.g. the two Re:ZERO OVAs) still have their own MAL id,
            // so the MAL id from the match wins over AniList's idMal to keep their metadata apart.
            var malId = match.MalId ?? (aniList?.IdMal is int idMal ? idMal : (long?)null);
            var jikan = malId.HasValue ? await _apiService.GetJikanAnimeAsync(malId.Value) : null;
            var primaryJikan = ShouldUseJikanForMovie(jikan) ? jikan : null;
            if (jikan != null && primaryJikan == null)
            {
                _logger.LogWarning("Ignoring Jikan metadata for movie '{Name}': MAL {MalId} is a '{Type}'", info.Name, malId, jikan.Type);
            }

            // With neither source answering (AniList rate limited, Jikan down) all that is left would be a hollow record
            // with a fallback title. Leave the movie alone instead; the next refresh tries again.
            if (aniList == null && primaryJikan == null)
            {
                _logger.LogWarning("No AniList or Jikan data for movie '{Name}' (AniList {AniListId}, MAL {MalId}); not writing metadata this time",
                    info.Name, match.AniListId, malId);
                return null;
            }

            var aniDbId = match.AniDbId;
            var (queryTitle, queryYear) = MovieIdentifier.ParseNameAndYear(info.Name, info.Year);

            // An AniList entry that covers several films (the Re:ZERO OVAs) names the whole set, not this film.
            var sharedEntry = aniList?.IdMal is int aniListMal && malId.HasValue && aniListMal != malId.Value;

            var metadata = new AnimeMetadata
            {
                AniDbId = aniDbId?.ToString(),
                AniListId = match.AniListId?.ToString(),
                MalId = malId?.ToString(),
                KitsuId = match.KitsuId?.ToString(),
                AniSearchId = match.AniSearchId?.ToString(),
                // All of these ids come from the anime-lists API, which keeps a film's own TMDb id (themoviedb_id) apart
                // from its parent show's (themoviedb_series_id). The two id spaces overlap: for Bodacious Space Pirates the
                // film is 239366 and the show 42501 is an unrelated 1971 film.
                TheMovieDbId = match.TmdbId?.ToString(),
                // Several films can share one IMDb id (Initial D Legend 1-3), so MovieIdentifier only passes on an unambiguous one.
                ImdbId = match.ImdbId,
                Type = aniList?.Format ?? primaryJikan?.Type
            };

            if (sharedEntry && primaryJikan == null)
            {
                var own = config.TitleField == Configuration.TitleFieldType.TitleEnglish ? match.EnglishTitle ?? match.Title : match.Title;
                metadata.Title = !string.IsNullOrWhiteSpace(own) ? own : queryTitle;
                metadata.OriginalTitle = string.Empty;
            }
            else
            {
                var titleSource = sharedEntry ? null : aniList;
                metadata.Title = await GetConfiguredTitle(primaryJikan, titleSource, config, info.MetadataLanguage, aniDbId, match.AniListId, queryTitle);
                metadata.OriginalTitle = GetConfiguredOriginalTitle(primaryJikan, titleSource, config);
            }

            metadata.CommunityRating = primaryJikan?.Score ?? (aniList?.AverageScore / 10.0);
            metadata.Overview = primaryJikan?.Synopsis ?? (aniList?.Description != null ? StripAnimeScheduleHtml(aniList.Description) : null);
            metadata.ReleaseDate = primaryJikan?.Aired?.From ?? ToDate(aniList?.StartDate);
            metadata.EndDate = primaryJikan?.Aired?.To ?? ToDate(aniList?.EndDate);
            metadata.Year = metadata.ReleaseDate?.Year ?? aniList?.StartDate?.Year ?? queryYear;
            metadata.Runtime = GetConfiguredRuntime(primaryJikan, aniList, config);
            metadata.ParentalRating = ChooseParentalRating(primaryJikan?.Rating)
                ?? await _apiService.GetKitsuParentalRatingAsync(match.KitsuId);
            metadata.Genres = _apiService.CombineGenres(primaryJikan, aniList, GetApprovedGenres(config));
            metadata.Studios = _apiService.GetStudios(jikan);

            try
            {
                metadata.People = match.AniListId.HasValue || malId.HasValue
                    ? await _apiService.GetPeopleAsync(match.AniListId ?? 0, malId ?? 0)
                    : new List<PersonInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching people for movie '{Name}'", info.Name);
                metadata.People = new List<PersonInfo>();
            }

            // AniDB models some film series as one entry (Initial D Legend 1-3 share AniDB 9987); they share its tags.
            metadata.Tags = config.EnableAniDbTags && aniDbId.HasValue
                ? await GetAniDbTagsForMovieAsync(aniDbId.Value, config)
                : new List<string>();

            _logger.LogInformation(
                "Populated movie '{Title}' ({Year}): {GenreCount} genres, {StudioCount} studios, {PeopleCount} people, {TagCount} tags",
                metadata.Title, metadata.Year, metadata.Genres?.Count ?? 0, metadata.Studios?.Count ?? 0, metadata.People?.Count ?? 0, metadata.Tags?.Count ?? 0);

            return metadata;
        }

        private async Task<List<string>> GetAniDbTagsForMovieAsync(long aniDbId, Configuration.PluginConfiguration config)
        {
            try
            {
                var tags = await _apiService.GetAniDbTagsAsync(aniDbId, config.AniDbClientName, config.AniDbClientVersion, config.AniDbRateLimit);
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
                _logger.LogError(ex, "Error fetching AniDB tags for AniDB id {AniDbId}", aniDbId);
                return new List<string>();
            }
        }

        // Jikan sends null (or an empty string) for titles MAL has not rated yet.
        private static string? ChooseParentalRating(string? rating) =>
            string.IsNullOrWhiteSpace(rating) ? null : rating.Trim();

        private static bool ShouldUseJikanForMovie(JikanAnime? jikan)
        {
            if (jikan == null)
            {
                return false;
            }

            // A film must not take its metadata from a TV series entry (a wrong MAL mapping would otherwise leak in).
            return string.IsNullOrWhiteSpace(jikan.Type)
                || !jikan.Type.StartsWith("TV", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime? ToDate(AniListDate? date) => EntryDetailsLoader.ToDate(date);

        /// <summary>
        /// The end date of the show's latest season - the date of its last episode. <c>Known</c> is false when the latest
        /// season could not be determined; <c>End</c> is null when it is known but still airing.
        /// </summary>
        private async Task<(bool Known, DateTime? End)> GetLatestSeasonEndAsync(Configuration.PluginConfiguration config, long? rootAniListId, CancellationToken cancellationToken)
        {
            if (!rootAniListId.HasValue || rootAniListId.Value > int.MaxValue)
            {
                return (false, null);
            }

            try
            {
                ApiService.AniListSeasonDetail? latest = null;

                // The API's latest season (the way TVDB/TMDb count them) - AniList's sequel chain can wander into another series.
                if (!string.IsNullOrWhiteSpace(config.EffectiveAnimeListsApiUrl))
                {
                    var franchise = await _animeListsApiClient.GetFranchiseAsync(config.EffectiveAnimeListsApiUrl, rootAniListId.Value, cancellationToken);
                    if (franchise?.LatestSeasonEntry?.AniListId is long latestId && latestId <= int.MaxValue)
                    {
                        latest = await _apiService.GetAniListSeasonDetailAsync((int)latestId);
                    }
                }

                latest ??= await _apiService.GetLatestSeasonDetailAsync((int)rootAniListId.Value);
                return latest == null ? (false, null) : (true, latest.EndDate);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not determine the end date of the latest season for AniList {AniListId}", rootAniListId);
                return (false, null);
            }
        }

        private static int _serviceCallCount = 0;

        public async Task<AnimeMetadata?> GetMetadataForSeries(SeriesInfo info, Configuration.PluginConfiguration config, CancellationToken cancellationToken = default)
        {
            var callId = Interlocked.Increment(ref _serviceCallCount);
            _logger.LogInformation("=== SERVICE CALL #{CallId} for: {Name} ===", callId, info.Name);

            var seriesPath = info.Path;

            // A .plexmatch file is always the first choice. Without one the series is identified from its folder name
            // (an id it already has, the anime-lists API, then AniList), and the result is fed into the same flow as a
            // .plexmatch that only carries an anilistid/malid.
            var plexMatchPath = Path.Combine(seriesPath, Constants.PlexMatchFileName);
            PlexMatchData plexMatchData;
            if (File.Exists(plexMatchPath))
            {
                _logger.LogInformation("Found .plexmatch file at {PlexMatchPath}", plexMatchPath);

                // Parse .plexmatch file
                var plexMatchContent = await File.ReadAllTextAsync(plexMatchPath);
                plexMatchData = _plexMatchParser.ParsePlexMatch(plexMatchContent);
            }
            else
            {
                var anchor = await _seriesIdentifier.IdentifyAsync(info, config, cancellationToken);
                if (anchor == null)
                {
                    // Check existing provider IDs (AniDB/TVDB) before giving up when API is off
                    var existingMapping = ResolveMappingFromProviderIds(info.ProviderIds);
                    if (existingMapping != null)
                    {
                        _logger.LogInformation("Series '{Name}' has no .plexmatch and anime-lists API is off, but resolved via provider IDs (AniDB/TVDB)", info.Name);
                        plexMatchData = new PlexMatchData { Title = info.Name, Year = info.Year };
                        // Skip the rest of identify flow and use existingMapping directly below
                    }
                    else
                    {
                        _logger.LogWarning("Series '{Name}' has no .plexmatch file and could not be identified from its name or provider IDs; skipping", info.Name);
                        return null;
                    }
                }
                else
                {
                    _logger.LogInformation("No .plexmatch for '{Name}'; identified via {Method} (AniList {AniListId}, MAL {MalId})",
                        info.Name, anchor.Method, anchor.AniListId, anchor.MalId);
                    plexMatchData = new PlexMatchData { Title = anchor.Title, Year = anchor.Year, AniListId = anchor.AniListId, MalId = anchor.MalId };
                }
            }

            _logger.LogInformation("Parsed .plexmatch data - Title: {Title}, Year: {Year}, TVDB: {TvdbId}, IMDb: {ImdbId}",
                plexMatchData.Title, plexMatchData.Year, plexMatchData.TvdbId, plexMatchData.ImdbId);

            // Ensure anime lists are loaded
            await _animeListMapper.LoadAnimeListsAsync();

            // Find mapping from .plexmatch or existing provider IDs
            AnimeMapping? mapping = null;
            
            // Try to resolve from existing provider IDs first (AniDB, TVDB)
            mapping = ResolveMappingFromProviderIds(info.ProviderIds);
            
            if (mapping == null && !string.IsNullOrEmpty(plexMatchData.TvdbId))
            {
                mapping = _manualMappings.FirstOrDefault(m => m.TvdbId.HasValue && m.TvdbId.Value.ToString() == plexMatchData.TvdbId)
                    ?? _animeListMapper.GetMappingByTvdbId(plexMatchData.TvdbId);
            }

            if (mapping == null && !string.IsNullOrEmpty(plexMatchData.ImdbId))
            {
                mapping = _manualMappings.FirstOrDefault(m =>
                    m.imdb_id.Any(id =>
                        string.Equals(id, plexMatchData.ImdbId, StringComparison.OrdinalIgnoreCase)))
                    ?? _animeListMapper.GetMappingByImdbId(plexMatchData.ImdbId);
            }

            // Direct AniList/MAL anchor: lets .plexmatch identify titles Fribb hasn't
            // cross-referenced to a TVDB/IMDb id yet (e.g. brand-new simulcasts).
            if (mapping == null && plexMatchData.AniListId.HasValue)
            {
                mapping = _animeListMapper.GetMappingByAniListId(plexMatchData.AniListId.Value)
                    ?? new AnimeMapping { anilist_id = plexMatchData.AniListId.Value };
                _logger.LogInformation("Resolved series '{Title}' via .plexmatch anilistid {AniListId} (no TVDB/IMDb id needed)",
                    plexMatchData.Title, plexMatchData.AniListId);
            }

            if (mapping == null && plexMatchData.MalId.HasValue)
            {
                mapping = _animeListMapper.GetMappingByMalId(plexMatchData.MalId.Value)
                    ?? new AnimeMapping { mal_id = plexMatchData.MalId.Value };
                _logger.LogInformation("Resolved series '{Title}' via .plexmatch malid {MalId} (no TVDB/IMDb id needed)",
                    plexMatchData.Title, plexMatchData.MalId);
            }

            if (mapping == null)
            {
                _logger.LogWarning("No mapping found for series '{Title}'", plexMatchData.Title);
                return null;
            }

            // Fribb sometimes has an anidb_id but hasn't cross-referenced anilist_id/mal_id yet for
            // a title. AnimeSchedule.net maintains its own independent AniDB/AniList/MAL cross-links
            // (when the user has configured a key) - use it to backfill whichever ids Fribb is
            // missing before we give up on AniList-driven enrichment (titles, seasons, tags, etc).
            _logger.LogInformation(
                "AnimeSchedule backfill check for '{Title}': anilist_id={AniListId}, anidb_id={AniDbId}, AnimeScheduleApiKey configured={HasKey} (len={KeyLen})",
                plexMatchData.Title, mapping.anilist_id, mapping.anidb_id, !string.IsNullOrWhiteSpace(config.AnimeScheduleApiKey), config.AnimeScheduleApiKey?.Length ?? 0);

            if (!mapping.anilist_id.HasValue && mapping.anidb_id.HasValue && !string.IsNullOrWhiteSpace(config.AnimeScheduleApiKey))
            {
                try
                {
                    var scheduleClient = new AnimeScheduleClient(_httpClient, _logger, config.AnimeScheduleApiKey);
                    var scheduleDetail = await scheduleClient.GetAnimeDetailByAniDbIdAsync(mapping.anidb_id.Value);
                    _logger.LogInformation(
                        "AnimeSchedule lookup for AniDB {AniDbId} returned: detail null={DetailNull}, AniList={AniListId}, MAL={MalId}",
                        mapping.anidb_id, scheduleDetail == null, scheduleDetail?.AniListId, scheduleDetail?.MalId);

                    if (scheduleDetail?.AniListId.HasValue == true || scheduleDetail?.MalId.HasValue == true)
                    {
                        mapping.anilist_id ??= scheduleDetail.AniListId;
                        mapping.mal_id ??= scheduleDetail.MalId;
                        _logger.LogInformation(
                            "Backfilled ids from AnimeSchedule for AniDB {AniDbId}: AniList {AniListId}, MAL {MalId}",
                            mapping.anidb_id, mapping.anilist_id, mapping.mal_id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to backfill ids from AnimeSchedule for AniDB id {AniDbId}", mapping.anidb_id);
                }
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

                // The anime-lists API knows the series the way TVDB and TMDb do, so its first season is the root - whatever
                // AniList's PREQUEL links say (there Dragon Ball Z is a sequel of Dragon Ball and Dragon Ball Super a
                // sequel of Dragon Ball Kai). When the API has an answer neither the PREQUEL walk nor the pinned-id list is needed.
                long? franchiseRoot = null;
                if (!string.IsNullOrWhiteSpace(config.EffectiveAnimeListsApiUrl))
                {
                    var franchise = await _animeListsApiClient.GetFranchiseAsync(config.EffectiveAnimeListsApiUrl, mappedAniListIdValue, cancellationToken);
                    franchiseRoot = franchise?.FirstSeasonAniListId;
                }

                var keepMapping = _keepAniListMappingIds.Contains(mappedAniListIdValue) || franchiseRoot.HasValue;

                if (franchiseRoot.HasValue && franchiseRoot.Value != mappedAniListIdValue)
                {
                    _logger.LogInformation("Using the first season of the TVDB/TMDb series, AniList {RootId}, instead of mapped AniList {MappedId} for '{Title}' (from the anime-lists API)",
                        franchiseRoot, mappedAniListIdValue, plexMatchData.Title);
                    rootAniListId = franchiseRoot;
                    aniListData = await _apiService.GetAniListAnimeAsync(franchiseRoot.Value) ?? aniListData;

                    var franchiseMapping = _animeListMapper.GetMappingByAniListId(franchiseRoot.Value);
                    if (franchiseMapping != null && franchiseMapping.TvdbId.HasValue && mapping.TvdbId.HasValue &&
                        franchiseMapping.TvdbId.Value == mapping.TvdbId.Value)
                    {
                        mapping = franchiseMapping;
                    }
                }

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
                        if (prequelMapping != null && prequelMapping.TvdbId.HasValue && mapping.TvdbId.HasValue &&
                            prequelMapping.TvdbId.Value == mapping.TvdbId.Value)
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
                        if (rootMapping != null && rootMapping.TvdbId.HasValue && mapping.TvdbId.HasValue &&
                            rootMapping.TvdbId.Value == mapping.TvdbId.Value)
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
                    rootAniListId = franchiseRoot ?? mapping.anilist_id;
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
            var latestSeasonEnd = await GetLatestSeasonEndAsync(config, rootAniListId, cancellationToken);
            return await CreateCombinedMetadata(plexMatchData, mapping, jikanData, aniListData, rootAniListId, effectiveMalId, config, info.MetadataLanguage, latestSeasonEnd);
        }

        private async Task<AnimeMetadata> CreateCombinedMetadata(
            PlexMatchData plexMatchData,
            AnimeMapping mapping,
            JikanAnime? jikanData,
            AniListMedia? aniListData,
            long? rootAniListId,
            long? effectiveMalId,
            Configuration.PluginConfiguration config,
            string? metadataLanguage,
            (bool Known, DateTime? End) latestSeasonEnd = default)
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

                // Provider IDs from .plexmatch; when it carries only an anilistid/malid (or there is no .plexmatch at all),
                // the mapping's ids fill the gap - the season and episode providers need the series' TVDB id.
                TvdbId = !string.IsNullOrEmpty(plexMatchData.TvdbId) ? plexMatchData.TvdbId : mapping.TvdbId?.ToString(),
                ImdbId = !string.IsNullOrEmpty(plexMatchData.ImdbId) ? plexMatchData.ImdbId : (mapping.imdb_id?.Length == 1 ? mapping.imdb_id[0] : null),

                // Provider IDs from mapping
                AniDbId = mapping.anidb_id?.ToString(),
                AniListId = rootAniListId?.ToString(),
                AniSearchId = mapping.anisearch_id?.ToString(),
                KitsuId = mapping.kitsu_id?.ToString(),
                MalId = effectiveMalId?.ToString(),
                TheMovieDbId = mapping.PreferredTmdbId?.ToString(),
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
            metadata.Title = await GetConfiguredTitle(primaryJikan, aniListPrimary, config, metadataLanguage, mapping.anidb_id, rootAniListId, plexMatchData.Title);
            metadata.OriginalTitle = GetConfiguredOriginalTitle(primaryJikan, aniListPrimary, config);

            // Status from Jikan, falling back to AniList's own status when Jikan has nothing usable
            // (e.g. an incomplete MAL mapping) - avoids defaulting to "not yet released" for a show
            // that's actually already aired and been downloaded.
            metadata.Status = _apiService.MapJikanStatus(primaryJikan?.Status);
            if (metadata.Status == "Unknown")
            {
                metadata.Status = _apiService.MapAniListStatus(aniListPrimary?.Status);
            }

            // Both Jikan and AniList's root/season-1 entry report THEIR OWN status, which for a
            // multi-season franchise is almost always "finished" even while a much later season is
            // still airing (Jellyfin just may not have a season folder for it yet). If the verdict
            // so far says Ended, double check by walking AniList's strict SEQUEL chain to whatever
            // the latest known entry is and prefer its status when that's still ongoing - this only
            // ever corrects a false "Ended", never overrides a genuinely correct one.
            if (metadata.Status == "Ended" && rootAniListId.HasValue)
            {
                var latestSeasonDetail = await _apiService.GetLatestSeasonDetailAsync((int)rootAniListId.Value);
                var latestStatus = _apiService.MapAniListStatus(latestSeasonDetail?.Status);
                if (latestStatus == "Continuing")
                {
                    metadata.Status = latestStatus;
                }
            }

            // Community Rating - convert AniList score (0-100) to 0-10 scale
            metadata.CommunityRating = primaryJikan?.Score ?? (aniListPrimary?.AverageScore / 10.0);

            // Overview - prefer Jikan, fallback to AniList, then AnimeSchedule.net (when configured)
            metadata.Overview = primaryJikan?.Synopsis ?? aniListPrimary?.Description;
            if (string.IsNullOrWhiteSpace(metadata.Overview) && rootAniListId.HasValue)
            {
                var scheduleDetail = await GetAnimeScheduleDetailAsync(config, rootAniListId.Value);
                if (!string.IsNullOrWhiteSpace(scheduleDetail?.Description))
                {
                    metadata.Overview = StripAnimeScheduleHtml(scheduleDetail!.Description!);
                }
            }

            // Release date: the first episode of the first season (Jikan, AniList when Jikan is out of reach).
            // End date: the last episode of the LATEST season - not the first season's end. While the latest season is
            // still airing there is no end date. If the latest season could not be determined at all, fall back to the
            // first season's end rather than leave it blank.
            metadata.ReleaseDate = primaryJikan?.Aired?.From ?? ToDate(aniListPrimary?.StartDate);
            metadata.EndDate = latestSeasonEnd.Known
                ? latestSeasonEnd.End
                : primaryJikan?.Aired?.To ?? ToDate(aniListPrimary?.EndDate);
            metadata.Year ??= metadata.ReleaseDate?.Year;

            // Runtime based on configuration
            metadata.Runtime = GetConfiguredRuntime(primaryJikan, aniListData, config);

            // Parental Rating: Jikan first; Kitsu when Jikan is down, paused or has no rating; and as a last resort the
            // rating of a Jikan entry we otherwise ignore (e.g. an ONA mapped to a TV season - same franchise, same rating).
            metadata.ParentalRating = ChooseParentalRating(primaryJikan?.Rating)
                ?? await _apiService.GetKitsuParentalRatingAsync(mapping.kitsu_id)
                ?? ChooseParentalRating(jikanData?.Rating);

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
        private static Configuration.TitleFieldType TitleFieldFromLanguage(string? language) =>
            language?.ToLowerInvariant() switch
            {
                "en" => Configuration.TitleFieldType.TitleEnglish,
                "ja" => Configuration.TitleFieldType.TitleJapanese,
                _ => Configuration.TitleFieldType.Title
            };

        private async Task<string> GetConfiguredTitle(
            JikanAnime? jikanData,
            AniListMedia? aniListData,
            Configuration.PluginConfiguration config,
            string? metadataLanguage,
            long? aniDbId,
            long? aniListId,
            string? fallbackTitle)
        {
            var languageField = TitleFieldFromLanguage(metadataLanguage);

            // 1. Try configured source+field.
            // 2. Try same configured field from any source.
            // 3. Try library language field from any source.
            // 4. Last resort: any title.
            var title = GetTitleFromSource(jikanData, aniListData, config.TitleField, config.TitleDataSource)
                ?? GetTitleFromJikan(jikanData, config.TitleField)
                ?? GetTitleFromAniList(aniListData, config.TitleField)
                ?? GetTitleFromJikan(jikanData, languageField)
                ?? GetTitleFromAniList(aniListData, languageField)
                ?? jikanData?.Title
                ?? aniListData?.Title?.Romaji;

            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            // Neither Jikan nor AniList had any data - typically because the Fribb mapping is
            // missing anilist_id/mal_id for a title it hasn't fully cross-referenced yet, even
            // though it matched via TVDB/IMDb/AniDB. Prefer AnimeSchedule.net when the user has
            // configured a key (a curated third vendor, and quicker/more reliable than AniDB's
            // own rate-limited API); fall back to AniDB's own title otherwise/next.
            if (aniListId.HasValue && !string.IsNullOrWhiteSpace(config.AnimeScheduleApiKey))
            {
                var scheduleDetail = await GetAnimeScheduleDetailAsync(config, aniListId.Value);
                var scheduleTitle = scheduleDetail?.Names?.English
                    ?? scheduleDetail?.Names?.Romaji
                    ?? scheduleDetail?.Names?.Native;
                if (!string.IsNullOrWhiteSpace(scheduleTitle))
                {
                    return scheduleTitle;
                }
            }

            if (aniDbId.HasValue)
            {
                try
                {
                    var aniDbTitle = await _apiService.GetAniDbMainTitleAsync(
                        aniDbId.Value, config.AniDbClientName, config.AniDbClientVersion, config.AniDbRateLimit);
                    if (!string.IsNullOrWhiteSpace(aniDbTitle))
                    {
                        return aniDbTitle;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch AniDB fallback title for AniDB id {AniDbId}", aniDbId);
                }
            }

            return !string.IsNullOrWhiteSpace(fallbackTitle) ? fallbackTitle : "Unknown Title";
        }

        // Shared by title/overview fallback tiers; cheap to call more than once since
        // AnimeScheduleClient caches by AniList id internally.
        private async Task<AnimeScheduleClient.AnimeDetail?> GetAnimeScheduleDetailAsync(Configuration.PluginConfiguration config, long aniListId)
        {
            if (string.IsNullOrWhiteSpace(config.AnimeScheduleApiKey))
            {
                return null;
            }

            try
            {
                var client = new AnimeScheduleClient(_httpClient, _logger, config.AnimeScheduleApiKey);
                return await client.GetAnimeDetailByAniListIdAsync(aniListId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch AnimeSchedule detail for AniList id {AniListId}", aniListId);
                return null;
            }
        }

        private static string StripAnimeScheduleHtml(string input)
        {
            var text = Regex.Replace(input, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<.*?>", string.Empty);
            return System.Net.WebUtility.HtmlDecode(text).Trim();
        }

        private string GetConfiguredOriginalTitle(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.PluginConfiguration config)
        {
            // Try configured source+field first, then same field from other sources, then any original title.
            return GetOriginalTitleFromSource(jikanData, aniListData, config.OriginalTitleField, config.OriginalTitleDataSource)
                ?? GetOriginalTitleFromJikan(jikanData, config.OriginalTitleField)
                ?? GetOriginalTitleFromAniList(aniListData, config.OriginalTitleField)
                ?? jikanData?.TitleJapanese
                ?? aniListData?.Title?.Native
                ?? string.Empty;
        }

        // Minutes per episode: the source the settings prefer (Jikan's duration or AniList's), the other as a fallback.
        private int? GetConfiguredRuntime(JikanAnime? jikanData, AniListMedia? aniListData, Configuration.PluginConfiguration config) =>
            _entryDetails.PickRuntime(jikanData, aniListData, config.RuntimeDataSource);

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
                Configuration.OriginalTitleFieldType.TitleEnglish => jikanData.TitleEnglish,
                Configuration.OriginalTitleFieldType.TitleRomaji => jikanData.Title,
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
                Configuration.OriginalTitleFieldType.TitleEnglish => aniListData.Title.English,
                Configuration.OriginalTitleFieldType.TitleRomaji => aniListData.Title.Romaji,
                Configuration.OriginalTitleFieldType.TitleJapanese => aniListData.Title.Native,
                _ => aniListData.Title.Native
            };
        }

        /// <summary>Resolves a mapping from existing provider IDs (AniDB, TVDB) without requiring the anime-lists API.</summary>
        private AnimeMapping? ResolveMappingFromProviderIds(IDictionary<string, string>? providerIds)
        {
            if (providerIds == null) return null;

            long? aniDbId = null;
            long? tvdbId = null;

            foreach (var pair in providerIds)
            {
                if (!long.TryParse(pair.Value, out var value) || value <= 0)
                {
                    continue;
                }

                if (string.Equals(pair.Key, Constants.AniDbProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    aniDbId = value;
                }
                else if (string.Equals(pair.Key, "Tvdb", StringComparison.OrdinalIgnoreCase) || 
                         string.Equals(pair.Key, "TheTVDB", StringComparison.OrdinalIgnoreCase))
                {
                    tvdbId = value;
                }
            }

            AnimeMapping? mapping = null;
            
            // Try TVDB first (has direct lookup in mapper)
            if (tvdbId.HasValue)
            {
                mapping = _manualMappings.FirstOrDefault(m => m.TvdbId.HasValue && m.TvdbId.Value == tvdbId.Value)
                    ?? _animeListMapper.GetMappingByTvdbId(tvdbId.Value.ToString());
            }

            // If no TVDB mapping and we have AniDB, try to find via manual mappings or the mapper
            if (mapping == null && aniDbId.HasValue)
            {
                mapping = _manualMappings.FirstOrDefault(m => m.anidb_id == aniDbId.Value)
                    ?? _animeListMapper.GetMappingByAniDbId(aniDbId.Value);
            }

            return mapping;
        }
    }
}
