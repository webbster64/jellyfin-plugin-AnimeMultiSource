using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // AniDB/MAL tag and genre data for a freshly-aired anime is often thin in its first week or two
    // (community tagging takes time to catch up) and keeps improving for months afterward. Rather
    // than requiring a manual "Refresh Metadata" click, this re-queries a series' metadata twice on
    // its own: about a week after its last episode was added, then again about 3 months later.
    public class AnimeTagRefreshTask : IScheduledTask
    {
        private const int FirstStageDays = 7;
        private const int SecondStageDays = 90;
        private const int WindowBufferDays = 3;
        private const string FirstStageKey = "7d";
        private const string SecondStageKey = "3mo";

        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IDirectoryService _directoryService;
        private readonly ILogger<AnimeTagRefreshTask> _logger;

        public AnimeTagRefreshTask(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IDirectoryService directoryService,
            ILogger<AnimeTagRefreshTask> logger)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _directoryService = directoryService;
            _logger = logger;
        }

        public string Name => "Refresh Recently-Aired Anime Tags";

        public string Key => "AnimeMultiSourceTagRefresh";

        public string Description => "Re-checks tags/genres/status about a week, then about 3 months, after a series' last episode was added - AniDB/MAL community tagging takes time to catch up.";

        public string Category => "Anime Multi Source";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            };
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var state = LoadState();

            var seriesQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true
            };
            var allSeries = _libraryManager.GetItemList(seriesQuery).OfType<Series>().ToList();

            var stateChanged = false;
            var refreshedCount = 0;

            for (var i = 0; i < allSeries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var series = allSeries[i];

                if (series.ProviderIds == null ||
                    (!series.ProviderIds.ContainsKey(Constants.AniDbProviderId) && !series.ProviderIds.ContainsKey(Constants.AniListProviderId)))
                {
                    progress.Report(100.0 * (i + 1) / allSeries.Count);
                    continue;
                }

                var episodeQuery = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    AncestorIds = new[] { series.Id },
                    Recursive = true
                };
                var realEpisodes = _libraryManager.GetItemList(episodeQuery).Where(e => !e.IsVirtualItem).ToList();
                if (realEpisodes.Count == 0)
                {
                    progress.Report(100.0 * (i + 1) / allSeries.Count);
                    continue;
                }

                var daysSinceLastAdded = (DateTime.UtcNow - realEpisodes.Max(e => e.DateCreated)).TotalDays;
                var seriesKey = series.Id.ToString("N");
                state.TryGetValue(seriesKey, out var completedStages);
                completedStages ??= new List<string>();

                string? stageToRun = null;
                if (IsInWindow(daysSinceLastAdded, FirstStageDays) && !completedStages.Contains(FirstStageKey))
                {
                    stageToRun = FirstStageKey;
                }
                else if (IsInWindow(daysSinceLastAdded, SecondStageDays) && !completedStages.Contains(SecondStageKey))
                {
                    stageToRun = SecondStageKey;
                }

                if (stageToRun != null)
                {
                    _logger.LogInformation(
                        "AnimeTagRefresh: refreshing {Series} ({Stage} stage, {Days:F0} days since last episode added)",
                        series.Name, stageToRun, daysSinceLastAdded);

                    _providerManager.QueueRefresh(
                        series.Id,
                        new MetadataRefreshOptions(_directoryService) { MetadataRefreshMode = MetadataRefreshMode.FullRefresh },
                        RefreshPriority.Normal);

                    completedStages.Add(stageToRun);
                    state[seriesKey] = completedStages;
                    stateChanged = true;
                    refreshedCount++;
                }

                progress.Report(100.0 * (i + 1) / allSeries.Count);
            }

            if (stateChanged)
            {
                SaveState(state);
            }

            _logger.LogInformation("AnimeTagRefresh: queued {Count} series for refresh", refreshedCount);
            return Task.CompletedTask;
        }

        private static bool IsInWindow(double daysSince, int targetDays)
        {
            return daysSince >= targetDays && daysSince <= targetDays + WindowBufferDays;
        }

        // Deliberately kept out of the series' own ProviderIds/Tags: AnimeMultiSourceProvider
        // rewrites both of those on every refresh this task itself triggers, which would erase a
        // marker stored there before the next run could ever see it. A small file in the plugin's
        // own data folder survives that refresh untouched.
        private Dictionary<string, List<string>> LoadState()
        {
            try
            {
                var path = GetStateFilePath();
                if (!File.Exists(path))
                {
                    return new Dictionary<string, List<string>>();
                }

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new Dictionary<string, List<string>>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeTagRefresh: failed to load state file; starting fresh");
                return new Dictionary<string, List<string>>();
            }
        }

        private void SaveState(Dictionary<string, List<string>> state)
        {
            try
            {
                File.WriteAllText(GetStateFilePath(), JsonSerializer.Serialize(state));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeTagRefresh: failed to save state file");
            }
        }

        private string GetStateFilePath()
        {
            string folder;
            try
            {
                var plugin = Plugin.Instance;
                var dataFolderProperty = plugin?.GetType().GetProperty("DataFolderPath");
                folder = dataFolderProperty?.GetValue(plugin) as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    throw new InvalidOperationException("DataFolderPath unavailable");
                }

                Directory.CreateDirectory(folder);
            }
            catch
            {
                folder = Path.Combine(AppContext.BaseDirectory, "AnimeMultiSourceCache");
                Directory.CreateDirectory(folder);
            }

            return Path.Combine(folder, "tag-refresh-state.json");
        }
    }
}
