using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
        // at the cost of more AnimeSchedule API calls per series per run (one per ISO week per
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
                !series.ProviderIds.TryGetValue(Constants.AniListProviderId, out var aniListIdRaw) ||
                !long.TryParse(aniListIdRaw, out var aniListId))
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
                var client = new AnimeScheduleClient(httpClient, _logger, config.AnimeScheduleApiKey);

                var route = await client.GetRouteByAniListIdAsync(aniListId);
                if (string.IsNullOrEmpty(route))
                {
                    _logger.LogInformation("AnimeSchedule: no route found for AniList id {AniListId} ({Series})", aniListId, series.Name);
                    return;
                }

                var entries = await GetUpcomingEntriesAsync(client, route, config.AnimeScheduleMode);
                if (entries.Count == 0)
                {
                    _logger.LogInformation("AnimeSchedule: no upcoming timetable entries found for {Series} (route {Route}, mode {Mode})", series.Name, route, config.AnimeScheduleMode);
                    return;
                }

                var season = await GetOrCreateCurrentSeasonAsync(series);

                var now = DateTime.UtcNow;
                foreach (var entry in entries)
                {
                    if (!entry.EpisodeNumber.HasValue || !entry.EpisodeDate.HasValue || entry.EpisodeDate.Value.UtcDateTime < now)
                    {
                        continue;
                    }

                    await ApplyEpisodeAsync(season, entry.EpisodeNumber.Value, entry.EpisodeDate.Value.UtcDateTime);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeSchedule: failed to update upcoming episodes for {Series}", series.Name);
            }
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

        // Sonarr (and similar tools) don't create a season folder until that season's first
        // episode has actually aired - meaning a brand-new/not-yet-aired show has no Season item
        // in Jellyfin at all yet, which is exactly when "Upcoming" is most useful. Create a
        // virtual Season 1 in that case rather than giving up, mirroring AddVirtualSeason from
        // jellyfin-plugin-tvdb.
        private async Task<Season> GetOrCreateCurrentSeasonAsync(Series series)
        {
            var existing = series.Children
                .OfType<Season>()
                .Where(s => (s.IndexNumber ?? 0) > 0)
                .OrderByDescending(s => s.IndexNumber)
                .FirstOrDefault();

            if (existing != null)
            {
                return existing;
            }

            const int seasonNumber = 1;
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

            series.AddChild(season);
            await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);

            _logger.LogInformation("AnimeSchedule: created virtual Season {SeasonNumber} for {Series} (no season folder exists yet)", seasonNumber, series.Name);
            return season;
        }

        private async Task ApplyEpisodeAsync(Season season, int episodeNumber, DateTime premiereDateUtc)
        {
            var existing = season.Children.OfType<Episode>().FirstOrDefault(e => e.IndexNumber == episodeNumber);
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
