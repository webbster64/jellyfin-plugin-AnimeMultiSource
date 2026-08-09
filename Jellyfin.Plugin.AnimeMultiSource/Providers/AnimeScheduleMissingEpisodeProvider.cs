using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Feeds AnimeSchedule.net air dates into Jellyfin's own "Upcoming" tab, which lists virtual
    // future episodes (IsVirtualItem = true) carrying a PremiereDate. Mirrors the approach the
    // official jellyfin-plugin-tvdb uses (TvdbMissingEpisodeProvider): a hosted service that
    // reacts to IProviderManager.RefreshCompleted rather than polling on a timer, so it re-checks
    // a series every time it's refreshed - the same trigger the rest of this plugin relies on.
    //
    // Scoped deliberately narrow: only the currently-airing (highest-numbered, non-specials)
    // season is touched, and only the current + next ISO week of timetable data is fetched. The
    // Upcoming tab only needs the next unaired episode's date, and AnimeSchedule's value over
    // TVDB is catching near-term delays, not projecting a full season ahead.
    public class AnimeScheduleMissingEpisodeProvider : IHostedService
    {
        private readonly IProviderManager _providerManager;
        private readonly ILogger<AnimeScheduleMissingEpisodeProvider> _logger;

        public AnimeScheduleMissingEpisodeProvider(
            IProviderManager providerManager,
            ILogger<AnimeScheduleMissingEpisodeProvider> logger)
        {
            _providerManager = providerManager;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _providerManager.RefreshCompleted += OnRefreshCompleted;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _providerManager.RefreshCompleted -= OnRefreshCompleted;
            return Task.CompletedTask;
        }

        private void OnRefreshCompleted(object? sender, GenericEventArgs<BaseItem> e)
        {
            if (e.Argument is not Series series)
            {
                return;
            }

            // Fire-and-forget: RefreshCompleted is a synchronous event, and there's nothing
            // for the caller to await here. Failures are logged inside HandleSeriesAsync.
            _ = HandleSeriesAsync(series);
        }

        private async Task HandleSeriesAsync(Series series)
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
                    _logger.LogDebug("AnimeSchedule: no route found for AniList id {AniListId} ({Series})", aniListId, series.Name);
                    return;
                }

                var entries = await GetUpcomingEntriesAsync(client, route, config.AnimeScheduleMode);
                if (entries.Count == 0)
                {
                    return;
                }

                var season = GetCurrentSeason(series);
                if (season == null)
                {
                    _logger.LogDebug("AnimeSchedule: no season found for series {Series}, skipping", series.Name);
                    return;
                }

                foreach (var entry in entries)
                {
                    if (!entry.EpisodeNumber.HasValue || !entry.EpisodeDate.HasValue)
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
        private async Task<List<AnimeScheduleClient.TimetableEntry>> GetUpcomingEntriesAsync(
            AnimeScheduleClient client, string route, AnimeScheduleModeType mode)
        {
            var now = DateTime.UtcNow;
            var weeks = new[] { now, now.AddDays(7) }
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

        private static Season? GetCurrentSeason(Series series)
        {
            return series.Children
                .OfType<Season>()
                .Where(s => (s.IndexNumber ?? 0) > 0)
                .OrderByDescending(s => s.IndexNumber)
                .FirstOrDefault();
        }

        private Task ApplyEpisodeAsync(Season season, int episodeNumber, DateTime premiereDateUtc)
        {
            var existing = season.Children.OfType<Episode>().FirstOrDefault(e => e.IndexNumber == episodeNumber);
            if (existing != null)
            {
                if (!existing.IsVirtualItem || existing.PremiereDate == premiereDateUtc)
                {
                    return Task.CompletedTask;
                }

                existing.PremiereDate = premiereDateUtc;
                return existing.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);
            }

            var episode = new Episode
            {
                Name = $"Episode {episodeNumber}",
                IndexNumber = episodeNumber,
                ParentIndexNumber = season.IndexNumber,
                SeriesId = season.SeriesId,
                SeasonId = season.Id,
                IsVirtualItem = true,
                PremiereDate = premiereDateUtc
            };

            season.AddChild(episode);
            _logger.LogInformation(
                "AnimeSchedule: added upcoming episode for {Series} S{Season}E{Episode} airing {Date}",
                season.Series?.Name, season.IndexNumber, episodeNumber, premiereDateUtc);
            return Task.CompletedTask;
        }
    }
}
