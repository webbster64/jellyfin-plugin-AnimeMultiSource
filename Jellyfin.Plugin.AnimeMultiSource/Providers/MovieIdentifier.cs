using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>The anime entry a movie was identified as.</summary>
    public sealed class MovieMatch
    {
        public long? AniListId { get; set; }

        public long? MalId { get; set; }

        public long? AniDbId { get; set; }

        public long? KitsuId { get; set; }

        public long? AniSearchId { get; set; }

        public long? TmdbId { get; set; }

        public string? ImdbId { get; set; }

        /// <summary>The matched entry's own titles; used when a source only has a combined entry for several films.</summary>
        public string? Title { get; set; }

        public string? EnglishTitle { get; set; }

        /// <summary>How it was identified: plexmatch, existing-id, api, anilist-search, existing-id-provider-ids.</summary>
        public string Method { get; set; } = string.Empty;

        public double Score { get; set; }

        public long? TvdbId { get; set; }
    }

    /// <summary>
    /// Identifies which anime a movie file is, without TMDb. Order (first hit wins):
    /// 1. a .plexmatch file in the movie's own folder (always the priority),
    /// 2. an AniList/MAL id the item already carries (set earlier, or chosen with "Identify"),
    /// 3. the anime-lists API title search (when configured),
    /// 4. AniList title search.
    /// Steps 3 and 4 refuse rather than guess when two entries are too close to call.
    /// </summary>
    public sealed class MovieIdentifier
    {
        private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".iso" };
        private static readonly Regex TrailingYear = new(@"^(.*?)\s*\((\d{4})\)\s*$", RegexOptions.Compiled);

        private readonly ILogger _logger;
        private readonly ApiService _apiService;
        private readonly AnimeListsApiClient _apiClient;
        private readonly PlexMatchParser _plexMatchParser;

        public MovieIdentifier(ILogger logger, ApiService apiService, AnimeListsApiClient apiClient, PlexMatchParser plexMatchParser)
        {
            _logger = logger;
            _apiService = apiService;
            _apiClient = apiClient;
            _plexMatchParser = plexMatchParser;
        }

        /// <summary>
        /// Identifies the movie, then completes the match with the other ids (AniDB for tags, TMDb/IMDb for artwork) from
        /// the anime-lists API. The movie path deliberately does not use the Fribb list: the API does the cross-checking.
        /// </summary>
        public async Task<MovieMatch?> IdentifyAsync(MovieInfo info, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var match = await IdentifyCoreAsync(info, config, cancellationToken).ConfigureAwait(false);
            if (match != null && match.Method != "api")
            {
                await CompleteFromApiAsync(match, config, cancellationToken).ConfigureAwait(false);
            }

            return match;
        }

        private async Task<MovieMatch?> IdentifyCoreAsync(MovieInfo info, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var (title, year) = ParseNameAndYear(info.Name, info.Year);

            // 1) .plexmatch next to the movie
            var plex = await TryReadPlexMatchAsync(info.Path).ConfigureAwait(false);
            if (plex != null)
            {
                var fromPlex = await MatchFromPlexMatchAsync(plex, info.Name, config, cancellationToken).ConfigureAwait(false);
                if (fromPlex != null)
                {
                    return fromPlex;
                }

                // A .plexmatch with only a title/year is still a better query than the folder name.
                if (!string.IsNullOrWhiteSpace(plex.Title))
                {
                    title = plex.Title;
                    year = plex.Year ?? year;
                }
            }

            // 2) an id the item already has
            var existing = MatchFromExistingIds(info);
            if (existing != null)
            {
                return existing;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            // 3) the anime-lists API: the only place ids are searched for. Without a URL, or while it is unreachable, the
            // movie stays unidentified (and is tried again on the next refresh).
            if (string.IsNullOrWhiteSpace(config.EffectiveAnimeListsApiUrl))
            {
                _logger.LogWarning("Movie '{Title}' ({Year}) has no .plexmatch and the anime-lists API is not switched on (Settings -> Anime-lists API); it cannot be identified", title, year);
                return null;
            }

            var candidates = await _apiClient.SearchAsync(config.EffectiveAnimeListsApiUrl, title, year, movie: true, cancellationToken).ConfigureAwait(false);
            if (candidates == null)
            {
                _logger.LogWarning("Movie '{Title}' ({Year}): the anime-lists API did not answer; trying again on the next refresh", title, year);
                return null;
            }

            var decision = MovieMatcher.Choose(title, year, candidates);
            if (!decision.Accepted && decision.Ranked.Count > 1)
            {
                // The API's synonym lists can be polluted by a sibling film's titles; AniList's names (fetched by the
                // API's own ids) are clean, so re-score the closest few with them before giving up.
                await AniListNameEnrichment.EnrichAsync(_apiService, decision.Ranked.Take(4).Select(r => r.Candidate)).ConfigureAwait(false);
                decision = MovieMatcher.Choose(title, year, candidates);
                if (decision.Accepted)
                {
                    _logger.LogDebug("'{Title}' resolved after re-scoring with AniList names", title);
                }
            }

            if (decision.Accepted)
            {
                return ToMatch(decision, "api", title, year);
            }

            LogRefusal(title, year, decision);
            return null;
        }

        /// <summary>Candidates for the "Identify" dialog (best first), from the anime-lists API.</summary>
        public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(string title, int? year, PluginConfiguration config, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.EffectiveAnimeListsApiUrl))
            {
                return Array.Empty<ScoredCandidate>();
            }

            var candidates = await _apiClient.SearchAsync(config.EffectiveAnimeListsApiUrl, title, year, movie: true, cancellationToken).ConfigureAwait(false);
            return candidates == null ? Array.Empty<ScoredCandidate>() : MovieMatcher.Rank(title, year, candidates);
        }

        private MovieMatch ToMatch(MatchDecision decision, string method, string title, int? year)
        {
            var best = decision.Best!.Candidate;
            _logger.LogInformation(
                "Movie '{Title}' ({Year}) identified as {Candidate} via {Method}: {Reason}",
                title, year, best.Describe(), method, decision.Reason);

            return FromCandidate(best, method, decision.Best.Score);
        }

        private static MovieMatch FromCandidate(AnimeCandidate best, string method, double score)
        {
            return new MovieMatch
            {
                AniListId = best.AniListId,
                MalId = best.MalId,
                AniDbId = best.AniDbId,
                KitsuId = best.KitsuId,
                AniSearchId = best.AniSearchId,
                TmdbId = best.TmdbId,
                // Several films can share one IMDb id (Initial D Legend 1-3); only trust it when it is unambiguous.
                ImdbId = best.ImdbIds.Count == 1 ? best.ImdbIds[0] : null,
                Title = best.Title ?? best.Romaji,
                EnglishTitle = best.English,
                Method = method,
                Score = score
            };
        }

        /// <summary>
        /// Fills the ids a match is still missing from the API's entry for it. Looks up by MAL id first (each film has its
        /// own, even when AniList lists several films as one entry), then by AniList id, and only trusts a single answer.
        /// </summary>
        private async Task CompleteFromApiAsync(MovieMatch match, PluginConfiguration config, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.EffectiveAnimeListsApiUrl) || match.AniDbId.HasValue)
            {
                return;
            }

            AnimeCandidate? entry = null;
            if (match.MalId.HasValue)
            {
                entry = await LookupSingleAsync(config, "mal_id", match.MalId.Value.ToString(), cancellationToken).ConfigureAwait(false);
            }

            if (entry == null && match.AniListId.HasValue)
            {
                entry = await LookupSingleAsync(config, "anilist_id", match.AniListId.Value.ToString(), cancellationToken).ConfigureAwait(false);
            }

            if (entry == null)
            {
                _logger.LogDebug("The anime-lists API has no single entry for AniList {AniList} / MAL {Mal}; keeping the ids we have", match.AniListId, match.MalId);
                return;
            }

            if (match.AniListId.HasValue && entry.AniListId.HasValue && match.AniListId != entry.AniListId)
            {
                return; // not the same entry; do not mix ids from two different anime
            }

            match.AniListId ??= entry.AniListId;
            match.MalId ??= entry.MalId;
            match.AniDbId = entry.AniDbId;
            match.KitsuId ??= entry.KitsuId;
            match.AniSearchId ??= entry.AniSearchId;
            match.TmdbId ??= entry.TmdbId;
            match.ImdbId ??= entry.ImdbIds.Count == 1 ? entry.ImdbIds[0] : null;
        }

        private async Task<AnimeCandidate?> LookupSingleAsync(PluginConfiguration config, string idName, string idValue, CancellationToken cancellationToken)
        {
            var found = await _apiClient.LookupAsync(config.EffectiveAnimeListsApiUrl, idName, idValue, cancellationToken).ConfigureAwait(false);
            return found is { Count: 1 } ? found[0] : null;
        }

        private void LogRefusal(string title, int? year, MatchDecision decision)
        {
            if (decision.Ranked.Count == 0)
            {
                _logger.LogWarning("Movie '{Title}' ({Year}): no matching anime found. Add a .plexmatch file with anilistid/malid to identify it.", title, year);
                return;
            }

            _logger.LogWarning(
                "Movie '{Title}' ({Year}) not identified: {Reason}. Closest: {Candidates}. Add a .plexmatch file with anilistid/malid to choose.",
                title, year, decision.Reason, string.Join("; ", decision.Ranked.Take(3).Select(r => $"{r.Candidate.Describe()} [{r.Score:F2}]")));
        }

        private MovieMatch? MatchFromExistingIds(MovieInfo info)
        {
            if (info.ProviderIds == null)
            {
                return null;
            }

            long? aniList = null, mal = null, aniDb = null, tvdb = null;
            foreach (var pair in info.ProviderIds)
            {
                if (!long.TryParse(pair.Value, out var value) || value <= 0)
                {
                    continue;
                }

                if (string.Equals(pair.Key, Constants.AniListProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    aniList = value;
                }
                else if (string.Equals(pair.Key, Constants.MalProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    mal = value;
                }
                else if (string.Equals(pair.Key, Constants.AniDbProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    aniDb = value;
                }
                else if (string.Equals(pair.Key, "Tvdb", StringComparison.OrdinalIgnoreCase) || 
                         string.Equals(pair.Key, "TheTVDB", StringComparison.OrdinalIgnoreCase))
                {
                    tvdb = value;
                }
            }

            // If we have AniList or MAL id, return immediately
            if (aniList.HasValue || mal.HasValue)
            {
                long? kitsu = TryGetId(info.ProviderIds, Constants.KitsuProviderId, out var k) ? k : null;
                _logger.LogDebug("Movie '{Name}' already has AniList {AniList} / MAL {Mal}; using it", info.Name, aniList, mal);
                return new MovieMatch { AniListId = aniList, MalId = mal, KitsuId = kitsu, Method = "existing-id", Score = 1.0 };
            }

            // No AniList/MAL id, but we have AniDB or TVDB - return a match with those ids so the service can resolve them
            _logger.LogDebug("Movie '{Name}' has existing AniDB {AniDb} / TVDB {Tvdb}; deferring resolution", info.Name, aniDb, tvdb);
            return new MovieMatch { AniListId = null, MalId = null, AniDbId = aniDb, TvdbId = tvdb, Method = "existing-id-provider-ids", Score = 1.0 };
        }

        private async Task<MovieMatch?> MatchFromPlexMatchAsync(PlexMatchData plex, string? name, PluginConfiguration config, CancellationToken cancellationToken)
        {
            // An AniList or MAL id is the answer as it stands; the other ids are filled in from the API afterwards.
            if (plex.AniListId.HasValue || plex.MalId.HasValue)
            {
                _logger.LogInformation("Movie '{Name}' identified via .plexmatch (AniList {AniListId}, MAL {MalId})", name, plex.AniListId, plex.MalId);
                return new MovieMatch { AniListId = plex.AniListId, MalId = plex.MalId, Method = "plexmatch", Score = 1.0 };
            }

            // A TVDB or IMDb id needs the API to become an anime id.
            if (string.IsNullOrWhiteSpace(config.EffectiveAnimeListsApiUrl))
            {
                return null;
            }

            AnimeCandidate? entry = null;
            if (!string.IsNullOrWhiteSpace(plex.ImdbId))
            {
                entry = await LookupSingleAsync(config, "imdb_id", plex.ImdbId, cancellationToken).ConfigureAwait(false);
                // An entry that lists several IMDb ids covers several films (Initial D Legend 1-3): it cannot say which one this is.
                if (entry != null && entry.ImdbIds.Count > 1)
                {
                    entry = null;
                }
            }

            if (entry == null && !string.IsNullOrWhiteSpace(plex.TvdbId))
            {
                entry = await LookupSingleAsync(config, "tvdb_id", plex.TvdbId, cancellationToken).ConfigureAwait(false);
            }

            if (entry == null || (!entry.AniListId.HasValue && !entry.MalId.HasValue))
            {
                return null;
            }

            _logger.LogInformation("Movie '{Name}' identified via .plexmatch tvdb/imdb id as {Candidate}", name, entry.Describe());
            return FromCandidate(entry, "plexmatch", 1.0);
        }

        private async Task<PlexMatchData?> TryReadPlexMatchAsync(string? moviePath)
        {
            if (string.IsNullOrWhiteSpace(moviePath))
            {
                return null;
            }

            try
            {
                var directory = Directory.Exists(moviePath) ? moviePath : Path.GetDirectoryName(moviePath);
                if (string.IsNullOrEmpty(directory))
                {
                    return null;
                }

                var plexMatchPath = Path.Combine(directory, Constants.PlexMatchFileName);
                if (!File.Exists(plexMatchPath))
                {
                    return null;
                }

                // A .plexmatch in a folder that holds several movies would give all of them the same identity.
                var videoCount = Directory.EnumerateFiles(directory)
                    .Count(f => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
                if (videoCount > 1)
                {
                    _logger.LogWarning("Ignoring {Path}: the folder holds {Count} video files, so it cannot belong to a single movie", plexMatchPath, videoCount);
                    return null;
                }

                return _plexMatchParser.ParsePlexMatch(await File.ReadAllTextAsync(plexMatchPath).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read .plexmatch for {Path}", moviePath);
                return null;
            }
        }

        /// <summary>Splits "Title (2019)" into title and year, preferring the year Jellyfin already parsed.</summary>
        public static (string Title, int? Year) ParseNameAndYear(string? name, int? parsedYear)
        {
            var title = (name ?? string.Empty).Trim();
            var match = TrailingYear.Match(title);
            if (match.Success)
            {
                title = match.Groups[1].Value.Trim();
                parsedYear ??= int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            return (title, parsedYear);
        }

        private static bool TryGetId(Dictionary<string, string> ids, string key, out long id)
        {
            id = 0;
            foreach (var pair in ids)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && long.TryParse(pair.Value, out id) && id > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
