using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers.ExternalIds
{
    public class AniDbExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "AniDB";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (item.TryGetProviderId(Constants.AniDbProviderId, out var aniDbId))
            {
                yield return $"https://anidb.net/anime/{aniDbId}";
            }
        }
    }

    public class AniListExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "AniList";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (item.TryGetProviderId(Constants.AniListProviderId, out var aniListId))
            {
                yield return $"https://anilist.co/anime/{aniListId}";
            }
        }
    }

    public class AniSearchExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "AniSearch";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (item.TryGetProviderId(Constants.AniSearchProviderId, out var aniSearchId))
            {
                yield return $"https://www.anisearch.com/anime/{aniSearchId}";
            }
        }
    }

    public class KitsuExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "Kitsu";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (item.TryGetProviderId(Constants.KitsuProviderId, out var kitsuId))
            {
                yield return $"https://kitsu.app/anime/{kitsuId}";
            }
        }
    }

    public class MalExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "MyAnimeList";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (item.TryGetProviderId(Constants.MalProviderId, out var malId))
            {
                yield return $"https://myanimelist.net/anime/{malId}";
            }
        }
    }
}
