using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    // Feeds AnimeSchedule.net air dates into Jellyfin's own "Upcoming" tab, which lists virtual
    // future episodes (IsVirtualItem = true) carrying a PremiereDate. Mirrors the approach the
    // official jellyfin-plugin-tvdb uses (TvdbMissingEpisodeProvider): a hosted service that
    // reacts to IProviderManager.RefreshCompleted rather than polling on a timer, so it re-checks
    // a series every time it's refreshed - the same trigger the rest of this plugin relies on.
    // AnimeScheduleRefreshTask covers the gap between refreshes (delayed/rescheduled episodes)
    // on a fixed schedule instead.
    public class AnimeScheduleMissingEpisodeProvider : IHostedService
    {
        private readonly IProviderManager _providerManager;
        private readonly AnimeScheduleUpcomingEpisodeUpdater _updater;

        public AnimeScheduleMissingEpisodeProvider(
            IProviderManager providerManager,
            ILibraryManager libraryManager,
            ILocalizationManager localization,
            ILogger<AnimeScheduleMissingEpisodeProvider> logger)
        {
            _providerManager = providerManager;
            _updater = new AnimeScheduleUpcomingEpisodeUpdater(libraryManager, localization, logger);
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

            // Block synchronously, same as the official jellyfin-plugin-tvdb missing-episode
            // provider does in its RefreshCompleted handler - a fire-and-forget Task here risked
            // running against a Series whose provider ids/children weren't fully written yet.
            _updater.RefreshSeriesAsync(series).GetAwaiter().GetResult();
        }
    }
}
