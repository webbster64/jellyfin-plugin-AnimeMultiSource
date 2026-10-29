using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Fills in shows and movies in libraries that use this plugin but still have no anime ids (added before the
    // plugin was installed, or a lookup that failed during a scan). It never refreshes the whole library: only
    // those items, one at a time, and one failing title never stops the rest. Titles that found no anime match
    // are only retried by "Fill in now", never by the timed run, so a mixed library doesn't re-refresh every
    // live-action film each time.
    public class AnimeFillMissing
    {
        private static readonly string[] AnimeIdKeys = { Constants.AniListProviderId, Constants.AniDbProviderId, Constants.MalProviderId };
        private static readonly SemaphoreSlim RunLock = new(1, 1);

        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IDirectoryService _directoryService;
        private readonly IActivityManager _activityManager;
        private readonly ILogger _logger;

        public AnimeFillMissing(ILibraryManager libraryManager, IProviderManager providerManager, IDirectoryService directoryService, IActivityManager activityManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _directoryService = directoryService;
            _activityManager = activityManager;
            _logger = logger;
        }

        public static bool IsDue(DateTime? lastRunUtc, int intervalHours, DateTime nowUtc)
        {
            // The timed task wakes up hourly; a few minutes of slack keep a 3-hour interval from slipping to 4 hours.
            return intervalHours > 0
                && (lastRunUtc == null || nowUtc - lastRunUtc.Value >= TimeSpan.FromHours(intervalHours) - TimeSpan.FromMinutes(5));
        }

        public async Task RunAsync(bool force, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.GetConfigurationSafe(_logger);
            var state = LoadState();
            if (!force && !IsDue(state.LastRunUtc, config.FillMissingIntervalHours, DateTime.UtcNow))
            {
                progress.Report(100);
                return;
            }

            if (!await RunLock.WaitAsync(0, cancellationToken))
            {
                _logger.LogInformation("Fill in missing anime metadata: already running; skipping this one");
                return;
            }

            // Claimed up front and saved in finally: a run that fails or is cancelled still counts,
            // or the hourly check would start it all over again an hour later.
            state.LastRunUtc = DateTime.UtcNow;
            try
            {
                var total = Stopwatch.StartNew();
                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie },
                    IsVirtualItem = false,
                    Recursive = true
                });

                var todo = items
                    .Where(i => !HasAnimeIds(i) && !ItemExclusion.IsExcluded(i, config) && UsesThisPlugin(i))
                    .Where(i => force || !state.Unmatched.ContainsKey(i.Id.ToString("N")))
                    .ToList();
                _logger.LogInformation("Fill in missing anime metadata ({Mode}): {Count} shows/movies to fill in", force ? "now" : $"every {config.FillMissingIntervalHours} h", todo.Count);

                var filled = new List<string>();
                var unmatched = new List<string>();
                for (var i = 0; i < todo.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = todo[i];
                    var itemTimer = Stopwatch.StartNew();

                    try
                    {
                        await _providerManager.RefreshFullItem(
                            item,
                            new MetadataRefreshOptions(_directoryService)
                            {
                                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                                ImageRefreshMode = MetadataRefreshMode.Default,
                                ReplaceAllMetadata = false
                            },
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Fill in missing anime metadata: refreshing {Name} failed", item.Name);
                    }

                    var refreshed = _libraryManager.GetItemById(item.Id) ?? item;
                    var key = item.Id.ToString("N");
                    if (HasAnimeIds(refreshed))
                    {
                        filled.Add(refreshed.Name);
                        state.Unmatched.Remove(key);
                    }
                    else
                    {
                        unmatched.Add(refreshed.Name);
                        state.Unmatched[key] = DateTime.UtcNow;
                    }

                    var done = i + 1;
                    var remaining = TimeSpan.FromTicks(total.Elapsed.Ticks / done * (todo.Count - done));
                    _logger.LogInformation("Fill in missing anime metadata [{Done}/{Count}] {Name}: {Outcome} ({Seconds:F1}s), about {Remaining:hh\\:mm\\:ss} left",
                        done, todo.Count, refreshed.Name, HasAnimeIds(refreshed) ? "filled in" : "no anime match", itemTimer.Elapsed.TotalSeconds, remaining);
                    progress.Report(100.0 * done / todo.Count);
                }

                progress.Report(100);
                var summary = $"Filled in {filled.Count} of {todo.Count} shows/movies missing anime metadata in {total.Elapsed:hh\\:mm\\:ss}";
                _logger.LogInformation("Fill in missing anime metadata: {Summary}", summary);

                // Only write to Activity when something happened, so an hourly no-op never fills it up.
                if (force || todo.Count > 0)
                {
                    await _activityManager.CreateAsync(new ActivityLog($"AnimeMultiSource: {summary}", "AnimeMultiSource", Guid.Empty)
                    {
                        ShortOverview = summary,
                        Overview = unmatched.Count > 0
                            ? "No anime match (check its .plexmatch or ids): " + string.Join(", ", unmatched.Take(50)) + (unmatched.Count > 50 ? ", ..." : string.Empty)
                            : null,
                        LogSeverity = unmatched.Count > 0 ? LogLevel.Warning : LogLevel.Information
                    });
                }
            }
            finally
            {
                SaveState(state);
                RunLock.Release();
            }
        }

        private static bool HasAnimeIds(BaseItem item)
            => item.ProviderIds != null && AnimeIdKeys.Any(k => item.ProviderIds.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v));

        // Only items whose library has AnimeMultiSource ticked as a metadata downloader for that type.
        private bool UsesThisPlugin(BaseItem item)
        {
            var options = _libraryManager.GetLibraryOptions(item);
            return item switch
            {
                Series => _providerManager.GetMetadataProviders<Series>(item, options, false).Any(p => p.Name == Constants.PluginName),
                Movie => _providerManager.GetMetadataProviders<Movie>(item, options, false).Any(p => p.Name == Constants.PluginName),
                _ => false
            };
        }

        // Kept in the plugin's data folder, not on the items: a refresh rewrites an item's ids.
        private FillMissingState LoadState()
        {
            try
            {
                var path = GetStatePath();
                return File.Exists(path) ? JsonSerializer.Deserialize<FillMissingState>(File.ReadAllText(path)) ?? new FillMissingState() : new FillMissingState();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fill in missing anime metadata: failed to load state file; starting fresh");
                return new FillMissingState();
            }
        }

        private void SaveState(FillMissingState state)
        {
            try
            {
                var path = GetStatePath();
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fill in missing anime metadata: failed to save state file");
            }
        }

        private static string GetStatePath()
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = Path.Combine(AppContext.BaseDirectory, "AnimeMultiSourceCache");
            }

            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "fill-missing-state.json");
        }

        public sealed class FillMissingState
        {
            public DateTime? LastRunUtc { get; set; }

            // Item id -> when it was last tried without finding an anime match.
            public Dictionary<string, DateTime> Unmatched { get; set; } = new();
        }
    }

    // Wakes up every hour and runs only when the interval chosen in the plugin settings has passed (or does nothing
    // when it's off). Checking hourly instead of encoding the interval in the trigger means a changed setting applies
    // straight away.
    public class AnimeFillMissingTask : IScheduledTask
    {
        private readonly AnimeFillMissing _fill;

        public AnimeFillMissingTask(ILibraryManager libraryManager, IProviderManager providerManager, IDirectoryService directoryService, IActivityManager activityManager, ILogger<AnimeFillMissingTask> logger)
        {
            _fill = new AnimeFillMissing(libraryManager, providerManager, directoryService, activityManager, logger);
        }

        public string Name => "Fill in missing anime metadata";

        public string Key => "AnimeMultiSourceFillMissing";

        public string Description => "Fills in shows and movies in Anime Multi Source libraries that have no anime ids yet, at the interval set in the plugin settings. Checks hourly; does nothing when it's off.";

        public string Category => "Anime Multi Source";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            };
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => _fill.RunAsync(force: false, progress, cancellationToken);
    }

    // "Fill in now" on the settings page starts this one: runs right away and also retries titles that found no match.
    public class AnimeFillMissingNowTask : IScheduledTask
    {
        private readonly AnimeFillMissing _fill;

        public AnimeFillMissingNowTask(ILibraryManager libraryManager, IProviderManager providerManager, IDirectoryService directoryService, IActivityManager activityManager, ILogger<AnimeFillMissingNowTask> logger)
        {
            _fill = new AnimeFillMissing(libraryManager, providerManager, directoryService, activityManager, logger);
        }

        public string Name => "Fill in missing anime metadata now";

        public string Key => "AnimeMultiSourceFillMissingNow";

        public string Description => "Fills in shows and movies that have no anime ids yet, right away, including titles that found no anime match before. A summary goes to Dashboard > Activity.";

        public string Category => "Anime Multi Source";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => _fill.RunAsync(force: true, progress, cancellationToken);
    }
}
