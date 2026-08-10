using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Shared by AnimeScheduleMissingEpisodeProvider (runs on series refresh) and
    // AnimeScheduleRefreshTask (runs on a schedule, to catch reschedules/delays that happen
    // between refreshes - Crunchyroll et al. regularly push an episode back a week).
    public class AnimeScheduleUpcomingEpisodeUpdater
    {
        // How far ahead to pull timetable data. Longer means more advance notice in Upcoming,
        // at the cost of more AnimeSchedule API calls per candidate per run (one per ISO week per
        // air-type queried; Combined mode queries both sub and dub). Episode titles aren't
        // available from AnimeSchedule's timetable at all (it's a scheduling calendar, not an
        // episode database) - virtual episodes get a "TBA" placeholder name, so pulling further
        // ahead doesn't risk showing a made-up title, only a date that may still shift.
        private const int LookaheadWeeks = 4;

        private readonly ILibraryManager _libraryManager;
        private readonly ILocalizationManager _localization;
        private readonly ILogger _logger;

        public AnimeScheduleUpcomingEpisodeUpdater(ILibraryManager libraryManager, ILocalizationManager localization, ILogger logger)
        {
            _libraryManager = libraryManager;
            _localization = localization;
            _logger = logger;
        }

        public async Task RefreshSeriesAsync(Series series)
        {
            var config = Plugin.GetConfigurationSafe(_logger);
            if (string.IsNullOrWhiteSpace(config.AnimeScheduleApiKey))
            {
                return;
            }

            if (series.ProviderIds == null ||
                !series.ProviderIds.TryGetValue(Constants.AniListProviderId, out var rootAniListIdRaw) ||
                !long.TryParse(rootAniListIdRaw, out var rootAniListId))
            {
                // Not a series this plugin mapped to AniList; nothing for us to add.
                return;
            }

            try
            {
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };
                using var httpClient = new HttpClient(handler);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-AnimeMultiSource-Plugin/1.0");
                var scheduleClient = new AnimeScheduleClient(httpClient, _logger, config.AnimeScheduleApiKey);
                var apiService = new ApiService(httpClient, _logger);

                var existingSeason = FindExistingCurrentSeason(series);

                // series.ProviderIds holds the ROOT AniList id (season 1's entry, used for the
                // Series-level metadata) - for multi-cour/multi-season anime that's a different
                // AniList entry than whatever is *currently* airing, and AnimeSchedule indexes each
                // cour separately just like AniList does. Prefer whichever season Jellyfin already
                // has folder evidence for (its own resolved AniList id, set by AnimeSeasonProvider),
                // falling back to root only when no season is foldered yet.
                var currentAniListId = rootAniListId;
                if (existingSeason?.ProviderIds != null &&
                    existingSeason.ProviderIds.TryGetValue(Constants.AniListProviderId, out var seasonAniListIdRaw) &&
                    long.TryParse(seasonAniListIdRaw, out var seasonAniListId))
                {
                    currentAniListId = seasonAniListId;
                }

                // Try the already-foldered/known season first, attaching straight to it rather than
                // parsing a season number from the route - some franchises air as ONE continuous
                // AniList entry with no season split at all (no "-2" suffix in the route) even
                // though Jellyfin/Sonarr's local folders split it into separate seasons by cour.
                // Trusting route-parsing here would misfile every such show's episodes back into
                // Season 1 forever. The folder Jellyfin already has is ground truth; only a
                // genuinely new, not-yet-foldered season (below) needs a guessed number at all.
                if (await TryApplyCandidateAsync(series, scheduleClient, config, currentAniListId, existingSeason))
                {
                    return;
                }

                // Nothing upcoming for the current season - check whether AniList has confirmed a
                // season beyond it via a strict, direct SEQUEL relation only (never the fuzzy
                // side-story/alternative-format fallback GetSeasonByNumberAsync uses elsewhere for
                // resolving a season number Jellyfin already has folder evidence for - a loose match
                // here risks attaching an unrelated side-story/spin-off's own schedule to this show as
                // a fake next season, confirmed as the cause of wrong season numbers and duplicate TBA
                // entries for shows with such relations in 1.0.6.7). The underlying AniList data is
                // cached 5 days regardless of outcome, so repeatedly asking is cheap even for a
                // franchise on a multi-year hiatus between seasons (Devil Is a Part-Timer: 2013 ->
                // 2022). Deliberately not gated on Series.Status == Ended: that field is TVDB-sourced
                // and often wrong for anime (the reason this plugin exists in the first place), and
                // was itself observed causing a currently-airing show to be skipped entirely.
                var currentSeasonDetail = await apiService.GetAniListSeasonDetailAsync((int)currentAniListId);
                var confirmedNextAniListId = currentSeasonDetail?.SequelAniListId;

                if (confirmedNextAniListId.HasValue &&
                    await TryApplyCandidateAsync(series, scheduleClient, config, confirmedNextAniListId.Value, knownSeason: null))
                {
                    return;
                }

                _logger.LogInformation("AnimeSchedule: no upcoming timetable entries found for {Series} across all candidate AniList ids", series.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeSchedule: failed to update upcoming episodes for {Series}", series.Name);
            }
        }

        // Resolves one AniList id to a route/timetable and, if it actually has upcoming entries,
        // applies them and returns true. Returns false (no side effects beyond the read-only
        // lookups) if this candidate has nothing upcoming, so the caller can move on to the next one.
        // knownSeason: pass the season Jellyfin already has folder evidence for to attach directly
        // to it (ground truth); pass null to derive the season number from AnimeSchedule's route
        // instead (only safe when there's no local evidence yet - see ParseSeasonNumberFromRoute).
        private async Task<bool> TryApplyCandidateAsync(
            Series series,
            AnimeScheduleClient scheduleClient,
            Configuration.PluginConfiguration config,
            long aniListId,
            Season? knownSeason)
        {
            var route = await scheduleClient.GetRouteByAniListIdAsync(aniListId);
            if (string.IsNullOrEmpty(route))
            {
                return false;
            }

            var entries = await GetUpcomingEntriesAsync(scheduleClient, route, config.AnimeScheduleMode);
            if (entries.Count == 0)
            {
                return false;
            }

            var season = knownSeason ?? await GetOrCreateSeasonAsync(series, ParseSeasonNumberFromRoute(route), aniListId);

            var now = DateTime.UtcNow;
            foreach (var entry in entries)
            {
                if (!entry.EpisodeNumber.HasValue || !entry.EpisodeDate.HasValue || entry.EpisodeDate.Value.UtcDateTime < now)
                {
                    continue;
                }

                await ApplyEpisodeAsync(season, entry.EpisodeNumber.Value, entry.EpisodeDate.Value.UtcDateTime);
            }

            return true;
        }

        // AniList's SEQUEL relation chain counts a hop for every entry, including a "part 2" cour
        // split that doesn't get its own season number in real-world (TVDB/Sonarr/AnimeSchedule)
        // numbering - e.g. Re:Zero's chain is root -> 2nd Season -> 2nd Season Part 2 -> 3rd Season
        // -> 4th Season, five hops for what's actually 4 seasons, because "Part 2" doesn't increment
        // the season count. Counting hops (as GetSeasonByNumberAsync does, for resolving a season
        // number Jellyfin already has folder evidence for) gets this off by one from that point on.
        // AnimeSchedule's own route naming already encodes the real season number directly, so
        // deriving it from there instead sidesteps the hop-counting mismatch entirely. Route naming
        // isn't fully consistent across shows though - Re:Zero uses a bare "...-4", but "Trapped in
        // a Dating Sim" season 2 uses "...-2nd-season" - so both trailing styles are handled; no
        // trailing season marker at all means the base/first season.
        private static int ParseSeasonNumberFromRoute(string route)
        {
            var withoutPartSuffix = Regex.Replace(route, "-part-\\d+$", string.Empty, RegexOptions.IgnoreCase);
            var match = Regex.Match(withoutPartSuffix, "-(\\d+)(?:st|nd|rd|th)?(?:-season)?$", RegexOptions.IgnoreCase);
            return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        }

        // Combined = prefer the dub timetable entry per episode number; fall back to sub only
        // for episode numbers that have no dub entry yet.
        private static async Task<List<AnimeScheduleClient.TimetableEntry>> GetUpcomingEntriesAsync(
            AnimeScheduleClient client, string route, AnimeScheduleModeType mode)
        {
            var now = DateTime.UtcNow;
            var weeks = Enumerable.Range(0, LookaheadWeeks)
                .Select(i => now.AddDays(7 * i))
                .Select(d => (Week: ISOWeek.GetWeekOfYear(d), Year: ISOWeek.GetYear(d)))
                .Distinct()
                .ToList();

            var byEpisodeNumber = new Dictionary<int, AnimeScheduleClient.TimetableEntry>();

            foreach (var (week, year) in weeks)
            {
                if (mode != AnimeScheduleModeType.Sub)
                {
                    var dubEntries = await client.GetTimetableAsync("dub", week, year);
                    foreach (var entry in dubEntries.Where(x => x.EpisodeNumber.HasValue && string.Equals(x.Route, route, StringComparison.OrdinalIgnoreCase)))
                    {
                        byEpisodeNumber[entry.EpisodeNumber!.Value] = entry;
                    }
                }

                if (mode != AnimeScheduleModeType.Dub)
                {
                    var subEntries = await client.GetTimetableAsync("sub", week, year);
                    foreach (var entry in subEntries.Where(x => x.EpisodeNumber.HasValue && string.Equals(x.Route, route, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (mode == AnimeScheduleModeType.Sub || !byEpisodeNumber.ContainsKey(entry.EpisodeNumber!.Value))
                        {
                            byEpisodeNumber[entry.EpisodeNumber!.Value] = entry;
                        }
                    }
                }
            }

            return byEpisodeNumber.Values.ToList();
        }

        private static Season? FindExistingCurrentSeason(Series series)
        {
            return series.Children
                .OfType<Season>()
                .Where(s => (s.IndexNumber ?? 0) > 0)
                // Exclude a season this updater speculatively created itself (virtual, with no
                // AniList id resolved for it - see GetOrCreateSeasonAsync) from being treated as
                // "the current season" on a later run. A real, folder-backed season (IsVirtualItem
                // false) or one AnimeSeasonProvider has already resolved an AniList id for both
                // qualify. Without this, a single wrong guess compounds forever: the next run picks
                // the bogus season back up as "existing", computes the next guess relative to IT
                // instead of the real season, and creates a second, even-more-wrong one on top of it.
                .Where(s => !s.IsVirtualItem || (s.ProviderIds != null && s.ProviderIds.ContainsKey(Constants.AniListProviderId)))
                .OrderByDescending(s => s.IndexNumber)
                .FirstOrDefault();
        }

        // Sonarr (and similar tools) don't create a season folder until that season's first
        // episode has actually aired - meaning a brand-new/not-yet-aired show, or a newly-detected
        // next season, has no Season item in Jellyfin at all yet, which is exactly when "Upcoming"
        // is most useful. Create a virtual season in that case rather than giving up, mirroring
        // AddVirtualSeason from jellyfin-plugin-tvdb. Only called once we know there's an episode
        // to attach to it.
        private async Task<Season> GetOrCreateSeasonAsync(Series series, int seasonNumber, long aniListId)
        {
            var existing = series.Children.OfType<Season>().FirstOrDefault(s => s.IndexNumber == seasonNumber);
            if (existing != null)
            {
                return existing;
            }

            string seasonName;
            try
            {
                seasonName = string.Format(CultureInfo.InvariantCulture, _localization.GetLocalizedString("NameSeasonNumber"), seasonNumber);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to localize season name for {Series}; using English fallback", series.Name);
                seasonName = $"Season {seasonNumber}";
            }

            var season = new Season
            {
                Name = seasonName,
                IndexNumber = seasonNumber,
                Id = _libraryManager.GetNewItemId($"{series.Id}{seasonNumber}{seasonName}", typeof(Season)),
                IsVirtualItem = true,
                SeriesId = series.Id,
                SeriesName = series.Name,
                SeriesPresentationUniqueKey = series.GetPresentationUniqueKey()
            };
            // Tag it with the AniList id it was resolved from so a later run's
            // FindExistingCurrentSeason recognizes this as a real, confirmed season instead of
            // speculative guesswork - see the exclusion filter there.
            season.SetProviderId(Constants.AniListProviderId, aniListId.ToString());

            series.AddChild(season);
            await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);

            _logger.LogInformation("AnimeSchedule: created virtual Season {SeasonNumber} for {Series} (no season folder exists yet)", seasonNumber, series.Name);
            return season;
        }

        private async Task ApplyEpisodeAsync(Season season, int episodeNumber, DateTime premiereDateUtc)
        {
            // season.Children is only a snapshot of whatever was already loaded onto this in-memory
            // object graph and isn't reliably populated with existing episodes; GetEpisodes() is a
            // real query (the same one jellyfin-plugin-tvdb uses for this exact lookup). Using
            // .Children here was creating a second, duplicate "TBA" virtual episode right next to a
            // real, already-named one whenever .Children came back empty.
            var existing = season.GetEpisodes().OfType<Episode>().FirstOrDefault(e => e.IndexNumber == episodeNumber);
            if (existing != null)
            {
                if (!existing.IsVirtualItem || existing.PremiereDate == premiereDateUtc)
                {
                    return;
                }

                existing.PremiereDate = premiereDateUtc;
                await existing.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
                _logger.LogInformation(
                    "AnimeSchedule: updated upcoming episode {Series} S{Season}E{Episode} to air {Date}",
                    season.Series?.Name, season.IndexNumber, episodeNumber, premiereDateUtc);
                return;
            }

            var seriesId = season.SeriesId;
            var episode = new Episode
            {
                // AnimeSchedule's timetable is a scheduling calendar, not an episode database - it
                // has no per-episode titles, so we don't guess one; matches how streaming services
                // themselves label an unreleased episode before its title is announced.
                Name = "TBA",
                IndexNumber = episodeNumber,
                ParentIndexNumber = season.IndexNumber,
                // A new BaseItem needs an explicit, deterministic Id before it can be persisted -
                // AddChild alone only updates the in-memory children list. Mirrors the seed string
                // jellyfin-plugin-tvdb's own AddVirtualEpisode uses.
                Id = _libraryManager.GetNewItemId($"{seriesId}{season.IndexNumber}Episode {episodeNumber}", typeof(Episode)),
                IsVirtualItem = true,
                SeasonId = season.Id,
                SeriesId = seriesId,
                SeriesName = season.Series?.Name,
                SeasonName = season.Name,
                SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey,
                PremiereDate = premiereDateUtc,
                DateLastSaved = DateTime.UtcNow
            };
            episode.PresentationUniqueKey = episode.GetPresentationUniqueKey();

            season.AddChild(episode);
            // AddChild only updates the in-memory tree; a direct repository save is what actually
            // persists it. Deliberately NOT QueueRefresh here (what jellyfin-plugin-tvdb uses for
            // its own new episodes): that queues the full normal metadata-provider pipeline for the
            // item, including TvdbEpisodeProvider - which then immediately overwrites the PremiereDate
            // we just set with TVDB's own (frequently wrong for anime, which is the whole reason this
            // plugin exists). UpdateToRepositoryAsync saves without re-triggering that pipeline.
            await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);

            _logger.LogInformation(
                "AnimeSchedule: added upcoming episode for {Series} S{Season}E{Episode} airing {Date}",
                season.Series?.Name, season.IndexNumber, episodeNumber, premiereDateUtc);
        }
    }
}
