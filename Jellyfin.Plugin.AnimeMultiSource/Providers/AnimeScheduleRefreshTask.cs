using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Runs on a fixed schedule (default: every 8 hours, so ~3x/day) rather than only on series
    // refresh, so a Crunchyroll-style "oops, pushed back a week" reschedule that happens between
    // refreshes still gets picked up without the user needing to manually refresh every series.
    public class AnimeScheduleRefreshTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<AnimeScheduleRefreshTask> _logger;
        private readonly AnimeScheduleUpcomingEpisodeUpdater _updater;

        public AnimeScheduleRefreshTask(ILibraryManager libraryManager, ILocalizationManager localization, ILogger<AnimeScheduleRefreshTask> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _updater = new AnimeScheduleUpcomingEpisodeUpdater(libraryManager, localization, logger);
        }

        public string Name => "Refresh AnimeSchedule Upcoming Episodes";

        public string Key => "AnimeMultiSourceAnimeScheduleRefresh";

        public string Description => "Re-checks AnimeSchedule.net for schedule changes (delays/reschedules) on anime this plugin has mapped, independent of library refreshes.";

        public string Category => "Anime Multi Source";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(8).Ticks
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true
            };

            var series = _libraryManager.GetItemList(query).OfType<Series>().ToList();
            _logger.LogInformation("AnimeSchedule refresh task: checking {Count} series", series.Count);

            for (var i = 0; i < series.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (series[i].ProviderIds != null && series[i].ProviderIds.ContainsKey(Constants.AniListProviderId))
                {
                    await _updater.RefreshSeriesAsync(series[i]);
                }

                progress.Report(100.0 * (i + 1) / series.Count);
            }
        }
    }
}
