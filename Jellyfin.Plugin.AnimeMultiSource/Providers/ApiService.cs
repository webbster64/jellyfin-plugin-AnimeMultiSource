using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using MediaBrowser.Controller.Entities; // For PersonInfo
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.AnimeMultiSource;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class ApiService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly TagFilterService _tagFilterService;
        private DateTime _lastAniDbRequest = DateTime.MinValue;
        private static readonly ConcurrentDictionary<long, AniDbCacheEntry> _aniDbCache = new();
        private static readonly TimeSpan AniDbCacheDuration = TimeSpan.FromDays(5);
        private static readonly object AniDbRateLock = new();
        private static DateTime _aniDbDailyCountDate = DateTime.UtcNow.Date;
        private static int _aniDbDailyRequestCount;
        private const int AniDbDailySoftCap = 5;
        private const int AniDbSlowRateLimitMs = 8000;
        private static readonly object AniListRateLock = new();
        private static DateTimeOffset _lastAniListRequest = DateTimeOffset.MinValue;
        private static int _aniListWindowCount;
        private static DateTimeOffset _aniListWindowStart = DateTimeOffset.UtcNow;
        private const int AniListMaxPerMinute = 30;
        private static readonly TimeSpan AniListCacheDuration = TimeSpan.FromDays(5);
        private static readonly ConcurrentDictionary<int, AniListCacheEntry<AniListMedia>> _aniListMediaCache = new();
        private static readonly ConcurrentDictionary<int, AniListCacheEntry<AniListSeasonDetail>> _aniListSeasonCache = new();
        private static readonly ConcurrentDictionary<int, AniListCacheEntry<int>> _aniListRootCache = new();
        private static readonly ConcurrentDictionary<int, AniListCacheEntry<List<PersonInfo>>> _aniListPeopleCache = new();
        private static readonly TimeSpan PersistentCacheMaxAge = TimeSpan.FromDays(5);
        private static readonly object PersistentCacheLock = new();
        private static bool _persistentCacheLoaded;
        private static string? _persistentCachePath;
        private static readonly JsonSerializerOptions CacheSerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };
        private static readonly object JikanRateLock = new();
        private static DateTimeOffset _lastJikanRequest = DateTimeOffset.MinValue;
        private const int JikanMinSpacingMs = 2500;
        private static readonly object AniDbBanLock = new();
        private static DateTimeOffset _aniDbBanUntil = DateTimeOffset.MinValue;
        private static string _aniDbBanReason = string.Empty;
        private static readonly TimeSpan AniDbBanBackoff = TimeSpan.FromMinutes(15);
        private const string VersionTag = "v0.0.1.3";

        public ApiService(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _tagFilterService = new TagFilterService();

            // Set user agent to be respectful to the APIs
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-AnimeMultiSource-Plugin/1.0");
        }

        public async Task<JikanAnime?> GetJikanAnimeAsync(long malId)
        {
            try
            {
                // Convert long to int for API call (MAL IDs should fit in int)
                if (malId > int.MaxValue)
                {
                    _logger.LogWarning("MAL ID {MalId} is too large for Jikan API", malId);
                    return null;
                }

                var intMalId = (int)malId;

                // Jikan API v4
                var url = $"https://api.jikan.moe/v4/anime/{intMalId}";
                _logger.LogDebug("Fetching Jikan data from: {Url}", url);

                var response = await SendJikanRequestAsync(url, "anime");
                if (response == null)
                {
                    return null;
                }

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var jikanResponse = JsonSerializer.Deserialize<JikanResponse>(json);
                    return jikanResponse?.Data;
                }
                else
                {
                    _logger.LogWarning("Jikan API returned status code: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Jikan data for MAL ID {MalId}", malId);
            }

            return null;
        }

        public async Task<AniListMedia?> GetAniListAnimeAsync(long anilistId)
        {
            try
            {
                // Convert long to int for API call (AniList IDs should fit in int)
                if (anilistId > int.MaxValue)
                {
                    _logger.LogWarning("AniList ID {AniListId} is too large for AniList API", anilistId);
                    return null;
                }

                var intAniListId = (int)anilistId;
                if (TryGetAniListCache(_aniListMediaCache, intAniListId, "media", out AniListMedia cachedMedia))
                {
                    return cachedMedia;
                }

                var query = @"
                    query ($id: Int) {
                        Media(id: $id) {
                            id
                            idMal
                            title {
                                romaji
                                english
                                native
                            }
                            description
                            genres
                            duration
                            averageScore
                            startDate {
                                year
                                month
                                day
                            }
                            endDate {
                                year
                                month
                                day
                            }
                            status
                            relations {
                                edges {
                                    relationType
                                    node {
                                        id
                                        type
                                        title {
                                            romaji
                                            english
                                        }
                                        format
                                        type
                                        episodes
                                        season
                                        seasonYear
                                    }
                                }
                            }
                        }
                    }";

                var variables = new { id = intAniListId };
                var request = new { query, variables };

                var jsonRequest = JsonSerializer.Serialize(request);
                var content = new StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");

                await EnforceAniListRateLimitAsync();
                var response = await _httpClient.PostAsync("https://graphql.anilist.co", content);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var aniListResponse = JsonSerializer.Deserialize<AniListResponse>(json);
                    var media = aniListResponse?.Data?.Media;
                    if (media != null)
                    {
                        StoreAniListCache(_aniListMediaCache, intAniListId, media, "media");
                    }
                    return media;
                }
                else
                {
                    _logger.LogWarning("AniList API returned status code: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching AniList data for AniList ID {AniListId}", anilistId);
            }

            return null;
        }

        public async Task<AniListSeasonDetail?> GetAniListSeasonDetailAsync(int aniListId)
        {
            if (TryGetAniListCache(_aniListSeasonCache, aniListId, "season", out AniListSeasonDetail cached))
            {
                return cached;
            }

            var query = @"
                query ($id: Int) {
                    Media(id: $id) {
                        id
                        idMal
                        title {
                            romaji
                            english
                            native
                        }
                        description
                        genres
                        averageScore
                        startDate { year month day }
                        endDate { year month day }
                        status
                        episodes
                        relations {
                            edges {
                                relationType
                                node { id format type }
                            }
                        }
                    }
                }";

            var variables = new { id = aniListId };
            var request = new { query, variables };
            var jsonRequest = JsonSerializer.Serialize(request);
            var content = new StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");

            await EnforceAniListRateLimitAsync();
            var response = await _httpClient.PostAsync("https://graphql.anilist.co", content);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AniList season fetch failed for {AniListId} with status {Status}", aniListId, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var aniListResponse = JsonSerializer.Deserialize<AniListResponse>(json);
            var media = aniListResponse?.Data?.Media;
            if (media == null)
            {
                return null;
            }

            var sequelId = SelectPreferredRelation(media.Relations?.Edges, "SEQUEL", tvOnly: true);

            var detail = new AniListSeasonDetail
            {
                AniListId = media.Id,
                MalId = media.IdMal,
                TitleRomaji = media.Title?.Romaji,
                TitleEnglish = media.Title?.English,
                TitleNative = media.Title?.Native,
                Description = media.Description,
                Genres = media.Genres ?? new List<string>(),
                AverageScore = media.AverageScore,
                StartDate = ToDateTime(media.StartDate),
                EndDate = ToDateTime(media.EndDate),
                Status = media.Status,
                Episodes = media.Episodes,
                SequelAniListId = sequelId,
                Format = media.Format,
                Type = media.Type,
                Relations = media.Relations?.Edges ?? new List<AniListRelationEdge>()
            };

            StoreAniListCache(_aniListSeasonCache, aniListId, detail, "season");
            return detail;
        }

        public async Task<AniListSeasonDetail?> GetSeasonByNumberAsync(int baseAniListId, int seasonNumber)
        {
            if (seasonNumber < 0)
            {
                return null;
            }

            if (seasonNumber == 0)
            {
                return await GetSpecialSeasonDetailAsync(baseAniListId);
            }

            if (seasonNumber == 1)
            {
                return await GetAniListSeasonDetailAsync(baseAniListId);
            }

            var currentId = baseAniListId;
            AniListSeasonDetail? detail = null;

            for (int i = 1; i <= seasonNumber; i++)
            {
                detail = await GetAniListSeasonDetailAsync(currentId);
                if (detail == null)
                {
                    return null;
                }

                if (i == seasonNumber)
                {
                    return detail;
                }

                if (!detail.SequelAniListId.HasValue)
                {
                    _logger.LogWarning("Missing sequel link after season {SeasonIndex} for AniList ID {AniListId}", i, currentId);
                    return null;
                }

                currentId = detail.SequelAniListId.Value;
            }

            return detail;
        }

        private async Task<AniListSeasonDetail?> GetSpecialSeasonDetailAsync(int baseAniListId)
        {
            var visited = new HashSet<int>();
            var currentId = baseAniListId;

            while (visited.Add(currentId))
            {
                var detail = await GetAniListSeasonDetailAsync(currentId);
                if (detail == null)
                {
                    return null;
                }

                var specialId = SelectSpecialRelation(detail.Relations);
                if (specialId.HasValue)
                {
                    _logger.LogInformation("Using special/OVA AniList ID {SpecialId} for base AniList ID {BaseId}", specialId, baseAniListId);
                    return await GetAniListSeasonDetailAsync(specialId.Value);
                }

                if (!detail.SequelAniListId.HasValue)
                {
                    break;
                }

                currentId = detail.SequelAniListId.Value;
            }

            _logger.LogDebug("No specials/OVAs found for base AniList ID {BaseId}", baseAniListId);
            return null;
        }

        public async Task<int> GetRootAniListIdAsync(long aniListId)
        {
            if (aniListId > int.MaxValue) return (int)aniListId;
            var startId = (int)aniListId;

            if (TryGetAniListCache(_aniListRootCache, startId, "root", out int cachedRoot))
            {
                return cachedRoot;
            }

            var visited = new HashSet<int>();
            var currentId = startId;

            while (!visited.Contains(currentId))
            {
                visited.Add(currentId);
                var media = await GetAniListAnimeAsync(currentId);
                if (media?.Relations?.Edges == null)
                {
                    break;
                }

                var prequelId = SelectPreferredRelation(media.Relations.Edges, "PREQUEL", tvOnly: true);
                if (prequelId.HasValue)
                {
                    currentId = prequelId.Value;
                    continue;
                }

                break;
            }

            StoreAniListCache(_aniListRootCache, startId, currentId, "root");
            return currentId;
        }

        public string? ParseJikanDuration(string? duration)
        {
            if (string.IsNullOrEmpty(duration)) return null;

            // Jikan returns duration like "24 min per ep", we just want the number
            var match = System.Text.RegularExpressions.Regex.Match(duration, @"(\d+)\s*min");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return null;
        }

        public string MapJikanStatus(string? jikanStatus)
        {
            return jikanStatus?.ToLower() switch
            {
                "currently airing" => "Continuing",
                "finished airing" => "Ended",
                "not yet aired" => "Not yet released",
                _ => jikanStatus ?? "Unknown"
            };
        }

        public List<string> CombineGenres(JikanAnime? jikanData, AniListMedia? aniListData, List<string> approvedGenres)
        {
            var allGenres = new List<string>();

            // Add genres from Jikan - filter out null names
            if (jikanData != null)
            {
                if (jikanData.Genres != null)
                    allGenres.AddRange(jikanData.Genres.Select(g => g.Name).Where(name => !string.IsNullOrEmpty(name))!);

                if (jikanData.Themes != null)
                    allGenres.AddRange(jikanData.Themes.Select(g => g.Name).Where(name => !string.IsNullOrEmpty(name))!);

                if (jikanData.ExplicitGenres != null)
                    allGenres.AddRange(jikanData.ExplicitGenres.Select(g => g.Name).Where(name => !string.IsNullOrEmpty(name))!);

                if (jikanData.Demographics != null)
                    allGenres.AddRange(jikanData.Demographics.Select(g => g.Name).Where(name => !string.IsNullOrEmpty(name))!);
            }

            // Add genres from AniList - filter out nulls
            if (aniListData?.Genres != null)
            {
                allGenres.AddRange(aniListData.Genres.Where(genre => !string.IsNullOrEmpty(genre)));
            }

            // Filter by approved genres if provided
            if (approvedGenres.Any())
            {
                allGenres = allGenres
                    .Where(genre => !string.IsNullOrEmpty(genre))
                    .Where(genre => approvedGenres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            return allGenres.Distinct().ToList();
        }

        public List<string> GetStudios(JikanAnime? jikanData)
        {
            var studios = new List<string>();

            if (jikanData?.Studios != null)
            {
                studios.AddRange(jikanData.Studios.Select(s => s.Name).Where(name => !string.IsNullOrEmpty(name))!);
            }

            return studios.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        }

        public async Task<List<PersonInfo>> GetPeopleAsync(long anilistId, long malId)
        {
            var people = new List<PersonInfo>();

            try
            {
                // Try AniList first for people data
                var aniListPeople = await GetAniListPeopleAsync(anilistId);
                if (aniListPeople != null)
                {
                    people.AddRange(aniListPeople);
                }

                // If we don't have enough people, try Jikan as fallback
                if (people.Count < 5 && malId > 0)
                {
                    var jikanPeople = await GetJikanPeopleAsync(malId);
                    if (jikanPeople != null)
                    {
                        // Merge without duplicates
                        foreach (var person in jikanPeople)
                        {
                            if (!people.Any(p => p.Name == person.Name && p.Role == person.Role))
                            {
                                people.Add(person);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching people data for AniList {AniListId}, MAL {MalId}", anilistId, malId);
            }

            return people;
        }

        private async Task<List<PersonInfo>> GetAniListPeopleAsync(long anilistId)
        {
            try
            {
                if (anilistId > int.MaxValue) return new List<PersonInfo>();
                var intAniListId = (int)anilistId;
                if (TryGetAniListCache(_aniListPeopleCache, intAniListId, "people", out List<PersonInfo> cached))
                {
                    return new List<PersonInfo>(cached);
                }

                var query = @"
                    query ($id: Int) {
                        Media(id: $id) {
                            characters(perPage: 50, sort: ROLE) {
                                edges {
                                    node {
                                        id
                                        name {
                                            full
                                        }
                                        image {
                                            large
                                        }
                                    }
                                    role
                                    voiceActors(language: JAPANESE) {
                                        id
                                        name {
                                            full
                                        }
                                        image {
                                            large
                                        }
                                    }
                                }
                            }
                            staff(perPage: 50, sort: RELEVANCE) {
                                edges {
                                    node {
                                        id
                                        name {
                                            full
                                        }
                                        image {
                                            large
                                        }
                                    }
                                    role
                                }
                            }
                        }
                    }";

                var variables = new { id = intAniListId };
                var request = new { query, variables };

                var jsonRequest = JsonSerializer.Serialize(request);
                var content = new StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");

                await EnforceAniListRateLimitAsync();
                var response = await _httpClient.PostAsync("https://graphql.anilist.co", content);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var aniListResponse = JsonSerializer.Deserialize<AniListResponse>(json);
                    var people = ParseAniListPeople(aniListResponse?.Data?.Media);
                    StoreAniListCache(_aniListPeopleCache, intAniListId, new List<PersonInfo>(people), "people");
                    return people;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching AniList people data for ID {AniListId}", anilistId);
            }

            return new List<PersonInfo>();
        }

        private async Task<List<PersonInfo>> GetJikanPeopleAsync(long malId)
        {
            try
            {
                if (malId > int.MaxValue) return new List<PersonInfo>();
                var intMalId = (int)malId;

                var url = $"https://api.jikan.moe/v4/anime/{intMalId}/characters";
                var response = await SendJikanRequestAsync(url, "people");

                if (response != null && response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var jikanResponse = JsonSerializer.Deserialize<JikanCharactersResponse>(json);
                    return ParseJikanPeople(jikanResponse?.Data);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Jikan people data for MAL ID {MalId}", malId);
            }

            return new List<PersonInfo>();
        }

        private List<PersonInfo> ParseAniListPeople(AniListMedia? media)
        {
            var people = new List<PersonInfo>();

            if (media?.Characters?.Edges != null)
            {
                foreach (var characterEdge in media.Characters.Edges)
                {
                    if (characterEdge?.Node == null)
                        continue;

                    // Only add voice actors; skip character entries
                    if (characterEdge.VoiceActors != null)
                    {
                        foreach (var va in characterEdge.VoiceActors)
                        {
                            people.Add(new PersonInfo
                            {
                                Name = va.Name?.Full ?? "Unknown VA",
                                Role = $"Voice - {characterEdge.Node.Name?.Full}",
                                ImageUrl = va.Image?.Large
                            });
                        }
                    }
                }
            }

            if (media?.Staff?.Edges != null)
            {
                foreach (var staffEdge in media.Staff.Edges)
                {
                    if (staffEdge?.Node != null)
                    {
                        people.Add(new PersonInfo
                        {
                            Name = staffEdge.Node.Name?.Full ?? "Unknown Staff",
                            Role = staffEdge.Role ?? "Staff",
                            ImageUrl = staffEdge.Node.Image?.Large
                        });
                    }
                }
            }

            return people;
        }

        private List<PersonInfo> ParseJikanPeople(List<JikanCharacter>? characters)
        {
            var people = new List<PersonInfo>();

            if (characters != null)
            {
                foreach (var character in characters)
                {
                    if (character?.Character == null)
                        continue;

                    // Only add voice actors; skip character entries
                    if (character.VoiceActors != null)
                    {
                        foreach (var va in character.VoiceActors)
                        {
                            people.Add(new PersonInfo
                            {
                                Name = va.Person?.Name ?? "Unknown VA",
                                Role = $"Voice - {character.Character.Name}",
                                // Type property is not available in Jellyfin 10.11.3
                                ImageUrl = va.Person?.Images?.WebP?.ImageUrl
                            });
                        }
                    }
                }
            }

            return people;
        }

        public List<SeasonRelation> GetSequelRelations(AniListMedia aniList)
        {
            var result = new List<SeasonRelation>();
            if (aniList?.Relations?.Edges == null) return result;

            foreach (var edge in aniList.Relations.Edges)
            {
                if (!string.Equals(edge?.RelationType, "SEQUEL", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (edge?.Node == null)
                    continue;

                var format = edge.Node.Format?.ToUpperInvariant() ?? string.Empty;
                if (format is not ("TV" or "TV_SHORT"))
                    continue;

                result.Add(new SeasonRelation
                {
                    RelationType = edge.RelationType ?? "SEQUEL",
                    AniListId = edge.Node.Id,
                    Title = edge.Node.Title?.Romaji,
                    TitleEnglish = edge.Node.Title?.English,
                    Format = edge.Node.Format,
                    Season = edge.Node.Season,
                    SeasonYear = edge.Node.SeasonYear,
                    Episodes = edge.Node.Episodes
                });
            }

            return result;
        }

        // Replace the GetAniDbTagsAsync method with this version that handles compression:
        public async Task<List<string>> GetAniDbTagsAsync(
            long anidbId,
            string clientName,
            string clientVersion,
            int rateLimitMs = 2000)
        {
            var now = DateTimeOffset.UtcNow;

            EnsurePersistentCacheLoaded();

            if (_aniDbCache.TryGetValue(anidbId, out var cached) &&
                now - cached.CachedAt < AniDbCacheDuration)
            {
                _logger.LogInformation("AniDB cache hit for ID {AniDbId} (age: {Age:F1} minutes)", anidbId, (now - cached.CachedAt).TotalMinutes);
                return new List<string>(cached.Tags);
            }

            if (IsAniDbTemporarilyBanned(out var remainingBan, out var banReason))
            {
                _logger.LogWarning("AniDB requests paused until {BanUntil:u} (remaining {Remaining:F1} minutes) because {Reason}", now + remainingBan, remainingBan.TotalMinutes, banReason);
                return new List<string>();
            }

            _aniDbCache.TryRemove(anidbId, out _);

            var tags = new List<string>();

            try
            {
                var rateContext = GetAniDbRateLimitContext(rateLimitMs);
                var timeSinceLastRequest = DateTime.UtcNow - _lastAniDbRequest;
                var waitMs = Math.Max(0, rateContext.EffectiveRateLimitMs - (int)timeSinceLastRequest.TotalMilliseconds);
                if (waitMs > 0)
                {
                    _logger.LogInformation("AniDB rate limiting: waiting {WaitMs} ms before request #{RequestNumber} (mode: {Mode}, date: {Date})",
                        waitMs,
                        rateContext.RequestNumber,
                        rateContext.IsSlowMode ? "slow" : "fast",
                        rateContext.Date.ToString("yyyy-MM-dd"));
                    await Task.Delay(waitMs);
                }
                else
                {
                    _logger.LogDebug("AniDB rate limiting: no wait needed before request #{RequestNumber} (mode: {Mode}, date: {Date})",
                        rateContext.RequestNumber,
                        rateContext.IsSlowMode ? "slow" : "fast",
                        rateContext.Date.ToString("yyyy-MM-dd"));
                }

                // AniDB HTTP API URL
                var url = $"http://api.anidb.net:9001/httpapi?request=anime&client={clientName}&clientver={clientVersion}&protover=1&aid={anidbId}";

                _logger.LogDebug("Fetching AniDB tags from: {Url}", url);

                var response = await _httpClient.GetAsync(url);
                _lastAniDbRequest = DateTime.UtcNow;

                if (IsAniDbRateLimited(response))
                {
                    var backoff = GetRetryAfterDelay(response, AniDbBanBackoff);
                    SetAniDbBan($"AniDB rate-limit response ({(int)response.StatusCode} {response.StatusCode})", backoff);
                    return tags;
                }

                if (response.IsSuccessStatusCode)
                {
                    string xmlContent;

                    // Try to read as string first (in case HttpClient already handled decompression)
                    try
                    {
                        xmlContent = await response.Content.ReadAsStringAsync();
                        _logger.LogDebug("Read AniDB response as string, length: {Length}", xmlContent.Length);
                    }
                    catch
                    {
                        // If reading as string fails, try manual decompression
                        _logger.LogDebug("Failed to read as string, trying manual decompression...");
                        byte[] responseData = await response.Content.ReadAsByteArrayAsync();

                        if (IsGzipped(responseData))
                        {
                            xmlContent = DecompressGzip(responseData);
                        }
                        else
                        {
                            xmlContent = System.Text.Encoding.UTF8.GetString(responseData);
                        }
                    }

                    _logger.LogDebug("Final AniDB response length: {Length} chars", xmlContent.Length);

                    if (ContainsAniDbBanMessage(xmlContent))
                    {
                        var snippet = xmlContent.Substring(0, Math.Min(xmlContent.Length, 160));
                        _logger.LogWarning("AniDB response looked like a ban/limit notice. First 160 chars: {Snippet}", snippet);
                        SetAniDbBan("AniDB response contained ban/limit notice", AniDbBanBackoff);
                        return tags;
                    }

                    // Parse XML response
                    var animeData = ParseAniDbXml(xmlContent);
                    if (animeData?.Tags?.TagList != null)
                    {
                        // Remove weight filter to get ALL tags, then filter through TagFilterService
                        var allTags = animeData.Tags.TagList
                            .Where(tag => !string.IsNullOrEmpty(tag.Name))
                            .Select(tag => tag.Name!)
                            .Distinct()
                            .ToList();

                        _logger.LogDebug("Found {Count} AniDB tags for ID {AniDbId} (no weight filter)", tags.Count, anidbId);

                        // Apply tag filtering
                        tags = _tagFilterService.FilterTags(allTags);
                        _tagFilterService.LogFilteredTags(_logger, allTags, tags);

                        _logger.LogDebug("Found {Count} filtered AniDB tags for ID {AniDbId}", tags.Count, anidbId);

                        var cacheEntry = new AniDbCacheEntry(DateTimeOffset.UtcNow, new List<string>(tags));
                        _aniDbCache[anidbId] = cacheEntry;
                        _logger.LogInformation("AniDB cache stored for ID {AniDbId} with {TagCount} tags", anidbId, tags.Count);
                        PersistCachesToDiskSafe();
                    }
                    else
                    {
                        _logger.LogWarning("No tags found in AniDB response for ID {AniDbId}", anidbId);
                    }
                }
                else
                {
                    _logger.LogWarning("AniDB API returned status code: {StatusCode}", response.StatusCode);

                    // Proactively back off on server busy/ban responses
                    if (IsAniDbRateLimited(response))
                    {
                        var backoff = GetRetryAfterDelay(response, AniDbBanBackoff);
                        SetAniDbBan("AniDB non-success rate-limit response", backoff);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching AniDB tags for ID {AniDbId}", anidbId);
            }

            return tags;
        }

        // Helper method to check if data is gzipped
        private bool IsGzipped(byte[] data)
        {
            // Gzip format starts with 0x1F 0x8B
            return data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B;
        }

        // Helper method to decompress gzipped data
        private string DecompressGzip(byte[] compressedData)
        {
            try
            {
                using var compressedStream = new MemoryStream(compressedData);
                using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
                using var reader = new StreamReader(gzipStream, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error decompressing gzipped response");
                throw;
            }
        }

        // Also update the TestAniDbConnectionAsync method to handle compression:
        public async Task<bool> TestAniDbConnectionAsync(string clientName, string clientVersion)
        {
            try
            {
                // Test with a known working anime (Natsume's Book of Friends - AniDB ID 5787)
                var testUrl = $"http://api.anidb.net:9001/httpapi?request=anime&client={clientName}&clientver={clientVersion}&protover=1&aid=5787";

                var response = await _httpClient.GetAsync(testUrl);
                if (response.IsSuccessStatusCode)
                {
                    byte[] responseData = await response.Content.ReadAsByteArrayAsync();
                    string content;

                    if (IsGzipped(responseData))
                    {
                        content = DecompressGzip(responseData);
                    }
                    else
                    {
                        content = System.Text.Encoding.UTF8.GetString(responseData);
                    }

                    if (!string.IsNullOrEmpty(content) && content.Contains("<anime>"))
                    {
                        _logger.LogInformation("AniDB API connection test successful");
                        return true;
                    }
                }

                _logger.LogWarning("AniDB API connection test failed. Status: {StatusCode}", response.StatusCode);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AniDB API connection test failed with exception");
                return false;
            }
        }

        private string CleanXmlContent(string xmlContent)
        {
            if (string.IsNullOrEmpty(xmlContent))
                return xmlContent;

            try
            {
                // Remove any invalid XML characters (like 0x1F)
                // Valid XML characters: #x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF]
                var validChars = xmlContent.Where(ch =>
                    ch == 0x9 ||
                    ch == 0xA ||
                    ch == 0xD ||
                    (ch >= 0x20 && ch <= 0xD7FF) ||
                    (ch >= 0xE000 && ch <= 0xFFFD) ||
                    (ch >= 0x10000 && ch <= 0x10FFFF)
                ).ToArray();

                var cleaned = new string(validChars);

                // Also try to detect if this might be an error message instead of XML
                if (cleaned.Contains("error") || cleaned.Contains("Error") || cleaned.Length < 10)
                {
                    _logger.LogWarning("AniDB response appears to be an error or too short: {Content}", cleaned);
                    return string.Empty;
                }

                return cleaned;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning XML content");
                return string.Empty;
            }
        }

        private AniDbAnime? ParseAniDbXml(string xmlContent)
        {
            if (string.IsNullOrEmpty(xmlContent))
                return null;

            try
            {
                // First, let's check if this looks like valid XML
                if (!xmlContent.TrimStart().StartsWith("<"))
                {
                    _logger.LogWarning("AniDB response doesn't start with XML tag: {First100Chars}", xmlContent.Substring(0, Math.Min(100, xmlContent.Length)));
                    return null;
                }

                var serializer = new XmlSerializer(typeof(AniDbAnime));
                using var reader = new StringReader(xmlContent);
                return (AniDbAnime?)serializer.Deserialize(reader);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing AniDB XML response. First 200 chars: {Content}",
                    xmlContent.Substring(0, Math.Min(200, xmlContent.Length)));
                return null;
            }
        }

        private AniDbRateLimitContext GetAniDbRateLimitContext(int configuredRateLimitMs)
        {
            lock (AniDbRateLock)
            {
                var today = DateTime.UtcNow.Date;
                if (today != _aniDbDailyCountDate)
                {
                    _aniDbDailyCountDate = today;
                    _aniDbDailyRequestCount = 0;
                    _logger.LogInformation("AniDB daily counter reset for {Date}", today.ToString("yyyy-MM-dd"));
                }

                var isSlowMode = _aniDbDailyRequestCount >= AniDbDailySoftCap;
                var effective = isSlowMode
                    ? Math.Max(AniDbSlowRateLimitMs, configuredRateLimitMs)
                    : configuredRateLimitMs;

                _aniDbDailyRequestCount++;
                return new AniDbRateLimitContext(effective, _aniDbDailyRequestCount, isSlowMode, _aniDbDailyCountDate);
            }
        }

        private bool IsAniDbTemporarilyBanned(out TimeSpan remaining, out string reason)
        {
            lock (AniDbBanLock)
            {
                var now = DateTimeOffset.UtcNow;
                if (now < _aniDbBanUntil)
                {
                    remaining = _aniDbBanUntil - now;
                    reason = _aniDbBanReason;
                    return true;
                }

                remaining = TimeSpan.Zero;
                reason = string.Empty;
                return false;
            }
        }

        private void SetAniDbBan(string reason, TimeSpan duration)
        {
            lock (AniDbBanLock)
            {
                var until = DateTimeOffset.UtcNow + duration;
                if (until > _aniDbBanUntil)
                {
                    _aniDbBanUntil = until;
                }

                _aniDbBanReason = reason;
                _logger.LogWarning("AniDB ban/backoff set for {Duration:F1} minutes due to {Reason}. Next attempt after {BanUntil:u}", duration.TotalMinutes, reason, _aniDbBanUntil);
            }
        }

        private bool IsAniDbRateLimited(HttpResponseMessage response)
        {
            return response.StatusCode == HttpStatusCode.Forbidden ||
                   response.StatusCode == HttpStatusCode.TooManyRequests ||
                   response.StatusCode == HttpStatusCode.ServiceUnavailable;
        }

        private bool ContainsAniDbBanMessage(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var lowered = content.ToLowerInvariant();

            // Valid AniDB responses include the <anime> root; don't treat those as ban/limit notices.
            if (lowered.Contains("<anime"))
            {
                return false;
            }

            // Avoid over-triggering on normal XML that happens to include "limit" tags
            var banPhrases = new[]
            {
                "banned",
                "temporary ban",
                "permanent ban",
                "ban " , // trailing space to avoid matching "bank"
                "ban-",
                "too many requests",
                "rate limit",
                "rate-limit",
                "try again later",
                "cooldown",
                "slow down"
            };

            return banPhrases.Any(p => lowered.Contains(p));
        }

        private TimeSpan GetRetryAfterDelay(HttpResponseMessage response, TimeSpan fallback)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter != null)
            {
                if (retryAfter.Delta.HasValue && retryAfter.Delta.Value > TimeSpan.Zero)
                {
                    return retryAfter.Delta.Value;
                }

                if (retryAfter.Date.HasValue)
                {
                    var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                    if (delta > TimeSpan.Zero)
                    {
                        return delta;
                    }
                }
            }

            return fallback;
        }

        private async Task EnforceJikanRateLimitAsync()
        {
            int waitMs;
            lock (JikanRateLock)
            {
                var now = DateTimeOffset.UtcNow;
                var sinceLast = now - _lastJikanRequest;
                waitMs = Math.Max(0, JikanMinSpacingMs - (int)sinceLast.TotalMilliseconds);
                _lastJikanRequest = now.AddMilliseconds(waitMs);
            }

            if (waitMs > 0)
            {
                _logger.LogDebug("Jikan rate limiting: waiting {WaitMs} ms before request", waitMs);
                await Task.Delay(waitMs);
            }
        }

        private async Task<HttpResponseMessage?> SendJikanRequestAsync(string url, string context)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await EnforceJikanRateLimitAsync();
                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    var backoff = AddJitter(GetRetryAfterDelay(response, TimeSpan.FromSeconds(Math.Min(30 * attempt, 90))));
                    _logger.LogWarning("Jikan rate limited for {Context} (status {Status}); backing off {DelayMs} ms before retry {Attempt}", context, response.StatusCode, backoff.TotalMilliseconds, attempt);
                    await Task.Delay(backoff);
                    continue;
                }

                return response;
            }

            _logger.LogWarning("Jikan request for {Context} to {Url} failed after retries", context, url);
            return null;
        }

        private TimeSpan AddJitter(TimeSpan baseDelay)
        {
            var jitterMs = Random.Shared.Next(200, 600);
            return baseDelay + TimeSpan.FromMilliseconds(jitterMs);
        }

        private async Task EnforceAniListRateLimitAsync()
        {
            int waitMs;
            int requestNumber;
            DateTimeOffset windowStart;
            lock (AniListRateLock)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _aniListWindowStart >= TimeSpan.FromMinutes(1))
                {
                    _aniListWindowStart = now;
                    _aniListWindowCount = 0;
                }

                _aniListWindowCount++;
                requestNumber = _aniListWindowCount;
                windowStart = _aniListWindowStart;
                var minSpacingMs = 60000 / AniListMaxPerMinute; // 2000 ms for 30/min
                var sinceLast = now - _lastAniListRequest;
                waitMs = Math.Max(0, minSpacingMs - (int)sinceLast.TotalMilliseconds);
                _lastAniListRequest = now.AddMilliseconds(waitMs);
            }

            if (waitMs > 0)
            {
                _logger.LogInformation("AniList rate limiting {Version}: waiting {WaitMs} ms before request #{CountInWindow} in current minute window starting {WindowStart}",
                    VersionTag, waitMs, requestNumber, windowStart.ToString("O"));
                await Task.Delay(waitMs);
            }
        }

        private int? SelectPreferredRelation(IEnumerable<AniListRelationEdge>? edges, string relationType, bool tvOnly = false)
        {
            if (edges == null) return null;

            int Score(AniListRelationEdge edge)
            {
                var format = edge.Node?.Format?.ToUpperInvariant() ?? string.Empty;
                return format switch
                {
                    "TV" => 3,
                    "TV_SHORT" => 2,
                    "ONA" => 1,
                    "OVA" => 0,
                    _ => -1
                };
            }

            var best = edges
                .Where(e =>
                    string.Equals(e?.RelationType, relationType, StringComparison.OrdinalIgnoreCase) &&
                    e?.Node != null &&
                    (!tvOnly || IsTvFormat(e.Node.Format)))
                .OrderByDescending(Score)
                .FirstOrDefault();

            return best?.Node?.Id;
        }

        private int? SelectSpecialRelation(IEnumerable<AniListRelationEdge>? edges)
        {
            if (edges == null) return null;

            int Score(AniListRelationEdge edge)
            {
                var relation = edge.RelationType?.ToUpperInvariant() ?? string.Empty;
                var format = edge.Node?.Format?.ToUpperInvariant() ?? string.Empty;
                var type = edge.Node?.Type?.ToUpperInvariant() ?? string.Empty;

                if (type != "ANIME")
                {
                    return -1;
                }

                var isSpecialFormat = format is "OVA" or "SPECIAL" or "ONA";
                if (!isSpecialFormat && relation != "SIDE_STORY")
                {
                    return -1;
                }

                if (relation == "SEQUEL" && format == "OVA")
                {
                    return 4;
                }

                if (relation == "SIDE_STORY" && format == "SPECIAL")
                {
                    return 3;
                }

                if (relation == "SIDE_STORY" && format == "OVA")
                {
                    return 2;
                }

                if (relation == "SEQUEL" && format == "SPECIAL")
                {
                    return 1;
                }

                return 0;
            }

            var best = edges
                .Where(edge => edge?.Node != null)
                .Select(edge => new { Edge = edge, Score = Score(edge!) })
                .Where(x => x.Score >= 0)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            return best?.Edge?.Node?.Id;
        }

        private bool IsTvFormat(string? format)
        {
            var upper = format?.ToUpperInvariant() ?? string.Empty;
            return upper == "TV" || upper == "TV_SHORT";
        }

        private DateTime? ToDateTime(AniListDate? date)
        {
            if (date?.Year == null || date.Month == null || date.Day == null)
            {
                return null;
            }

            try
            {
                return new DateTime(date.Year.Value, date.Month.Value, date.Day.Value, 0, 0, 0, DateTimeKind.Utc);
            }
            catch
            {
                return null;
            }
        }

        private bool TryGetAniListCache<T>(ConcurrentDictionary<int, AniListCacheEntry<T>> cache, int key, string cacheName, out T value)
        {
            EnsurePersistentCacheLoaded();

            if (cache.TryGetValue(key, out var entry))
            {
                var age = DateTimeOffset.UtcNow - entry.CachedAt;
                if (age < AniListCacheDuration)
                {
                    _logger.LogInformation("AniList cache hit {Version} for {CacheName} {AniListId} (age: {Age:F1} minutes)", VersionTag, cacheName, key, age.TotalMinutes);
                    value = entry.Data;
                    return true;
                }

                cache.TryRemove(key, out _);
            }

            value = default!;
            return false;
        }

        private void StoreAniListCache<T>(ConcurrentDictionary<int, AniListCacheEntry<T>> cache, int key, T value, string cacheName)
        {
            cache[key] = new AniListCacheEntry<T>(DateTimeOffset.UtcNow, value);
            _logger.LogDebug("AniList cache stored for {CacheName} {AniListId}", cacheName, key);
            PersistCachesToDiskSafe();
        }

        private void EnsurePersistentCacheLoaded()
        {
            if (_persistentCacheLoaded)
            {
                return;
            }

            lock (PersistentCacheLock)
            {
                if (_persistentCacheLoaded)
                {
                    return;
                }

                try
                {
                    LoadPersistentCacheFromDisk();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load persistent cache from disk");
                }
                finally
                {
                    _persistentCacheLoaded = true;
                }
            }
        }

        private void LoadPersistentCacheFromDisk()
        {
            var path = GetPersistentCachePath();
            if (!File.Exists(path))
            {
                _logger.LogDebug("No persistent cache file found at {Path}", path);
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                var snapshot = JsonSerializer.Deserialize<PersistentCacheSnapshot>(json, CacheSerializerOptions);
                if (snapshot == null)
                {
                    return;
                }

                RestoreCache(_aniDbCache, snapshot.AniDb);
                RestoreCache(_aniListMediaCache, snapshot.AniListMedia);
                RestoreCache(_aniListSeasonCache, snapshot.AniListSeason);
                RestoreCache(_aniListRootCache, snapshot.AniListRoot);
                RestoreCache(_aniListPeopleCache, snapshot.AniListPeople);

                _logger.LogInformation("Persistent cache loaded from {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error loading persistent cache from {Path}", path);
            }
        }

        private void PersistCachesToDiskSafe()
        {
            try
            {
                PersistCachesToDisk();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist caches to disk");
            }
        }

        private void PersistCachesToDisk()
        {
            var path = GetPersistentCachePath();
            var now = DateTimeOffset.UtcNow;

            var snapshot = new PersistentCacheSnapshot
            {
                AniDb = _aniDbCache
                    .Where(kvp => now - kvp.Value.CachedAt < PersistentCacheMaxAge)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                AniListMedia = _aniListMediaCache
                    .Where(kvp => now - kvp.Value.CachedAt < PersistentCacheMaxAge)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                AniListSeason = _aniListSeasonCache
                    .Where(kvp => now - kvp.Value.CachedAt < PersistentCacheMaxAge)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                AniListRoot = _aniListRootCache
                    .Where(kvp => now - kvp.Value.CachedAt < PersistentCacheMaxAge)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                AniListPeople = _aniListPeopleCache
                    .Where(kvp => now - kvp.Value.CachedAt < PersistentCacheMaxAge)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };

            lock (PersistentCacheLock)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(snapshot, CacheSerializerOptions);
                File.WriteAllText(path, json);
                _logger.LogDebug("Persistent cache saved to {Path}", path);
            }
        }

        private void RestoreCache<TKey>(ConcurrentDictionary<TKey, AniDbCacheEntry> target, Dictionary<TKey, AniDbCacheEntry>? source)
            where TKey : notnull
        {
            if (source == null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var kvp in source)
            {
                if (now - kvp.Value.CachedAt < PersistentCacheMaxAge)
                {
                    target[kvp.Key] = kvp.Value;
                }
            }
        }

        private void RestoreCache<TKey, TValue>(ConcurrentDictionary<TKey, AniListCacheEntry<TValue>> target, Dictionary<TKey, AniListCacheEntry<TValue>>? source)
            where TKey : notnull
        {
            if (source == null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var kvp in source)
            {
                if (now - kvp.Value.CachedAt < PersistentCacheMaxAge)
                {
                    target[kvp.Key] = kvp.Value;
                }
            }
        }

        private string GetPersistentCachePath()
        {
            if (!string.IsNullOrWhiteSpace(_persistentCachePath))
            {
                return _persistentCachePath!;
            }

            lock (PersistentCacheLock)
            {
                if (!string.IsNullOrWhiteSpace(_persistentCachePath))
                {
                    return _persistentCachePath!;
                }

                var folder = ResolveCacheFolder();
                _persistentCachePath = Path.Combine(folder, "provider-cache.json");
                return _persistentCachePath;
            }
        }

        private string ResolveCacheFolder()
        {
            try
            {
                var plugin = Plugin.Instance;
                var dataFolderProperty = plugin?.GetType().GetProperty("DataFolderPath");
                var folderPath = dataFolderProperty?.GetValue(plugin) as string;
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                    return folderPath!;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve plugin data folder, falling back to base directory");
            }

            var fallback = Path.Combine(AppContext.BaseDirectory, "AnimeMultiSourceCache");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        private sealed class PersistentCacheSnapshot
        {
            public Dictionary<long, AniDbCacheEntry> AniDb { get; set; } = new();
            public Dictionary<int, AniListCacheEntry<AniListMedia>> AniListMedia { get; set; } = new();
            public Dictionary<int, AniListCacheEntry<AniListSeasonDetail>> AniListSeason { get; set; } = new();
            public Dictionary<int, AniListCacheEntry<int>> AniListRoot { get; set; } = new();
            public Dictionary<int, AniListCacheEntry<List<PersonInfo>>> AniListPeople { get; set; } = new();
        }

        private sealed record AniDbCacheEntry(DateTimeOffset CachedAt, List<string> Tags);
        private sealed record AniDbRateLimitContext(int EffectiveRateLimitMs, int RequestNumber, bool IsSlowMode, DateTime Date);
        private sealed record AniListCacheEntry<T>(DateTimeOffset CachedAt, T Data);
        public sealed class AniListSeasonDetail
        {
            public int AniListId { get; set; }
            public int? MalId { get; set; }
            public string? TitleRomaji { get; set; }
            public string? TitleEnglish { get; set; }
            public string? TitleNative { get; set; }
            public string? Description { get; set; }
            public List<string> Genres { get; set; } = new();
            public int? AverageScore { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public string? Status { get; set; }
            public int? Episodes { get; set; }
            public int? SequelAniListId { get; set; }
            public string? Format { get; set; }
            public string? Type { get; set; }
            public List<AniListRelationEdge> Relations { get; set; } = new();
        }
    }
}
