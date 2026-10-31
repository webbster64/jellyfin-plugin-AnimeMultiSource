using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>The anime a series folder was identified as: the anchor the .plexmatch flow would otherwise read from the file.</summary>
    public sealed class SeriesAnchor
    {
        public long? AniListId { get; set; }

        public long? MalId { get; set; }

        /// <summary>existing-id, api, or api-root (tied entries resolved to their common first season).</summary>
        public string Method { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }
    }

    /// <summary>
    /// Identifies a series that has no .plexmatch file (a .plexmatch is always read first, by the caller). Order:
    /// 1. an AniList/MAL/AniDB/TVDB id the item already carries (stored earlier, or chosen with "Identify"),
    /// 2. the anime-lists API title search - the only place ids are searched for.
    /// It refuses rather than guesses when two unrelated entries are too close to call.
    /// </summary>
    public sealed class SeriesIdentifier
    {
        private readonly ILogger _logger;
        private readonly ApiService _apiService;
        private readonly AnimeListsApiClient _apiClient;

        public SeriesIdentifier(ILogger logger, ApiService apiService, AnimeListsApiClient apiClient)
        {
            _logger = logger;
            _apiService = apiService;
            _apiClient = apiClient;
        }

        public async Task<SeriesAnchor?> IdentifyAsync(SeriesInfo info, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var (title, year) = MovieIdentifier.ParseNameAndYear(info.Name, info.Year);

            // 1) an id the series already has
            var existing = FromIds(info.ProviderIds, title, year);
            if (existing != null)
            {
                _logger.LogInformation("Series '{Title}' already has AniList {AniList} / MAL {Mal}; using it", title, existing.AniListId, existing.MalId);
                return existing;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            // 2) the anime-lists API
            if (string.IsNullOrWhiteSpace(config.EffectiveAnimeListsApiUrl))
            {
                _logger.LogWarning("Series '{Title}' has no .plexmatch and the anime-lists API is not switched on (Settings -> Anime-lists API); it cannot be identified", title);
                return null;
            }

            var candidates = await _apiClient.SearchAsync(config.EffectiveAnimeListsApiUrl, title, year, movie: false, cancellationToken).ConfigureAwait(false);
            if (candidates == null)
            {
                _logger.LogWarning("Series '{Title}': the anime-lists API did not answer; trying again on the next refresh", title);
                return null;
            }

            if (candidates.Count > 1)
            {
                // The API can lack an English title or carry a sibling's synonyms, which makes the wrong entry look like the
                // only exact match (the two Haruhi series: neither has an English title there, and the 2009 one's romaji
                // field is literally "The Melancholy of Haruhi Suzumiya (2009)"). So before deciding, the closest entries get
                // AniList's own names, fetched by the API's ids; no id comes from AniList.
                var closest = SeriesMatcher.Rank(title, year, candidates).Take(4).Select(r => r.Candidate).ToList();
                await AniListNameEnrichment.EnrichAsync(_apiService, closest).ConfigureAwait(false);
            }

            var anchor = await TryDecideAsync(title, year, candidates, config.EffectiveAnimeListsApiUrl, cancellationToken).ConfigureAwait(false);
            if (anchor == null)
            {
                LogRefusal(title, year, SeriesMatcher.Rank(title, year, candidates));
            }

            return anchor;
        }

        /// <summary>
        /// Shows for the "Identify" dialog, best first: one entry per series as TVDB/TMDb see it, not one per season or
        /// special. A typed AniList or MAL id is looked up directly and answers with the show it belongs to.
        /// </summary>
        public async Task<IReadOnlyList<RemoteSearchResult>> SearchAsync(SeriesInfo searchInfo, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            if (string.IsNullOrWhiteSpace(config.EffectiveAnimeListsApiUrl))
            {
                return results;
            }

            var byId = FromIds(searchInfo.ProviderIds, searchInfo.Name ?? string.Empty, searchInfo.Year);
            if (byId != null)
            {
                var franchise = byId.AniListId.HasValue
                    ? await _apiClient.GetFranchiseByPlatformAsync(config.EffectiveAnimeListsApiUrl, "anilist", byId.AniListId.Value, cancellationToken).ConfigureAwait(false)
                    : await _apiClient.GetFranchiseByPlatformAsync(config.EffectiveAnimeListsApiUrl, "mal", byId.MalId!.Value, cancellationToken).ConfigureAwait(false);
                if (franchise?.FirstSeasonEntry != null)
                {
                    results.Add(ToShowResult(franchise));
                }

                return results;
            }

            // A typed TVDB or TMDb id (the dialog offers those fields too).
            foreach (var (key, platform) in new[] { ("Tvdb", "tvdb"), ("Tmdb", "tmdb") })
            {
                if (searchInfo.ProviderIds != null
                    && searchInfo.ProviderIds.TryGetValue(key, out var typed)
                    && long.TryParse(typed, out var typedId) && typedId > 0)
                {
                    var typedFranchise = await _apiClient.GetFranchiseByPlatformAsync(config.EffectiveAnimeListsApiUrl, platform, typedId, cancellationToken).ConfigureAwait(false);
                    if (typedFranchise?.FirstSeasonEntry != null)
                    {
                        results.Add(ToShowResult(typedFranchise));
                        return results;
                    }
                }
            }

            var (title, year) = MovieIdentifier.ParseNameAndYear(searchInfo.Name, searchInfo.Year);
            if (string.IsNullOrWhiteSpace(title))
            {
                return results;
            }

            var shows = await _apiClient.SearchSeriesAsync(config.EffectiveAnimeListsApiUrl, title, year, 10, cancellationToken).ConfigureAwait(false);

            // The search lists some shows with only their season 0 (Dragon Ball, Dragon Ball Z and Dragon Ball Super come
            // back that way: their main entry is one long AniList entry split into TVDB seasons), while asking for the
            // show by its TVDB/TMDb id returns it complete. Complete those; what still has no season after that is only a
            // few specials, not a show.
            var completed = await Task.WhenAll((shows ?? Array.Empty<ApiFranchise>()).Select(f => CompleteAsync(config.EffectiveAnimeListsApiUrl, f, cancellationToken))).ConfigureAwait(false);
            results.AddRange(completed.Where(f => f.FirstSeasonEntry != null).Select(ToShowResult));
            return results;
        }

        private async Task<ApiFranchise> CompleteAsync(string apiUrl, ApiFranchise franchise, CancellationToken cancellationToken)
        {
            if (franchise.FirstSeasonEntry != null)
            {
                return franchise;
            }

            ApiFranchise? full = null;
            if (franchise.TvdbId.HasValue)
            {
                full = await _apiClient.GetFranchiseByPlatformAsync(apiUrl, "tvdb", franchise.TvdbId.Value, cancellationToken).ConfigureAwait(false);
            }
            else if (franchise.TmdbId.HasValue)
            {
                full = await _apiClient.GetFranchiseByPlatformAsync(apiUrl, "tmdb", franchise.TmdbId.Value, cancellationToken).ConfigureAwait(false);
            }

            return full?.FirstSeasonEntry != null ? full : franchise;
        }

        private async Task<SeriesAnchor?> TryDecideAsync(string title, int? year, IReadOnlyList<AnimeCandidate> candidates, string apiUrl, CancellationToken cancellationToken)
        {
            var ranked = SeriesMatcher.Rank(title, year, candidates);
            var decision = SeriesMatcher.Decide(ranked);
            if (decision.Accepted)
            {
                _logger.LogInformation("Series '{Title}' ({Year}) identified as {Candidate} via the API: {Reason}",
                    title, year, decision.Best!.Candidate.Describe(), decision.Reason);
                return new SeriesAnchor { AniListId = decision.Best.Candidate.AniListId, MalId = decision.Best.Candidate.MalId, Method = "api", Title = title, Year = year };
            }

            // A franchise listed as several entries (seasons, remakes of the same show) ties on the title. If every tied
            // entry leads back to the same first season it is one series; if not (two real roots) it is ambiguous.
            var tied = SeriesMatcher.TiedLeaders(ranked);
            if (tied.Count >= 2 && tied.All(t => t.Candidate.AniListId.HasValue))
            {
                var roots = new HashSet<long>();
                foreach (var entry in tied)
                {
                    // The first season of the TVDB/TMDb series the API places the entry in; AniList's PREQUEL walk only when
                    // the API has no answer (it would wrongly lead Dragon Ball Z to Dragon Ball).
                    var franchise = await _apiClient.GetFranchiseAsync(apiUrl, entry.Candidate.AniListId!.Value, cancellationToken).ConfigureAwait(false);
                    roots.Add(franchise?.FirstSeasonAniListId
                        ?? await _apiService.GetRootAniListIdAsync(entry.Candidate.AniListId!.Value).ConfigureAwait(false));
                }

                if (roots.Count == 1)
                {
                    var root = roots.First();
                    _logger.LogInformation("Series '{Title}' ({Year}): {Count} close candidates all lead back to AniList {Root}", title, year, tied.Count, root);
                    return new SeriesAnchor { AniListId = root, Method = "api-root", Title = title, Year = year };
                }
            }

            return null;
        }

        private static SeriesAnchor? FromIds(IDictionary<string, string>? ids, string title, int? year)
        {
            if (ids == null)
            {
                return null;
            }

            long? aniList = null, mal = null, aniDb = null, tvdb = null;
            foreach (var pair in ids)
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

            return aniList.HasValue || mal.HasValue || aniDb.HasValue || tvdb.HasValue
                ? new SeriesAnchor { AniListId = aniList, MalId = mal, Method = "existing-id", Title = title, Year = year }
                : null;
        }

        // One Identify entry per show (the TVDB/TMDb series), carrying the ids of its first season - the entry the rest of
        // the plugin treats as the series - plus the show's own TVDB/TMDb ids.
        private static RemoteSearchResult ToShowResult(ApiFranchise franchise)
        {
            var entry = franchise.FirstSeasonEntry!;
            var seasons = franchise.Seasons.Where(s => s.Number >= 1).ToList();
            var episodes = seasons.Sum(s => s.Episodes ?? 0);

            var result = new RemoteSearchResult
            {
                Name = entry.English ?? franchise.Title ?? entry.Title ?? entry.Romaji ?? entry.Native,
                ProductionYear = franchise.Year ?? entry.Year,
                SearchProviderName = Constants.PluginName,
                Overview = string.Join(" · ", new[]
                {
                    seasons.Count == 1 ? "1 season" : seasons.Count + " seasons",
                    episodes > 0 ? episodes + " episodes" : null,
                    entry.Romaji != null && entry.Romaji != entry.English ? entry.Romaji : null,
                    "AniList " + entry.AniListId
                }.Where(part => !string.IsNullOrWhiteSpace(part)))
            };

            void Set(string key, long? value)
            {
                if (value.HasValue)
                {
                    result.ProviderIds[key] = value.Value.ToString();
                }
            }

            Set(Constants.AniListProviderId, entry.AniListId);
            Set(Constants.MalProviderId, entry.MalId);
            Set(Constants.AniDbProviderId, entry.AniDbId);
            Set(Constants.KitsuProviderId, entry.KitsuId);
            Set(Constants.AniSearchProviderId, entry.AniSearchId);
            Set("Tvdb", franchise.TvdbId);
            Set("Tmdb", franchise.TmdbId);
            return result;
        }

        private void LogRefusal(string title, int? year, IReadOnlyList<ScoredCandidate> ranked)
        {
            if (ranked.Count == 0)
            {
                _logger.LogWarning("Series '{Title}' ({Year}): the anime-lists API has no matching anime. Add a .plexmatch file with anilistid/malid, or use Identify.", title, year);
                return;
            }

            _logger.LogWarning(
                "Series '{Title}' ({Year}) not identified: no clear leader among the API's answers. Closest: {Candidates}. Add a .plexmatch file with anilistid/malid, or use Identify.",
                title, year, string.Join("; ", ranked.Take(3).Select(r => $"{r.Candidate.Describe()} [{r.Score:F2}]")));
        }
    }
}
