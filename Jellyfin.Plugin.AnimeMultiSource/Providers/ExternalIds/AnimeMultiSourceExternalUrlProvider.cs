using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers.ExternalIds
{
    public class AnimeMultiSourceExternalUrlProvider : IExternalUrlProvider
    {
        public string Name => "Anime Multi-Source";

        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            // AniDB
            if (item.TryGetProviderId(Constants.AniDbProviderId, out var aniDbId))
            {
                yield return $"https://anidb.net/anime/{aniDbId}";
            }

            // AniList
            if (item.TryGetProviderId(Constants.AniListProviderId, out var aniListId))
            {
                yield return $"https://anilist.co/anime/{aniListId}";
            }

            // AniSearch
            if (item.TryGetProviderId(Constants.AniSearchProviderId, out var aniSearchId))
            {
                yield return $"https://www.anisearch.com/anime/{aniSearchId}";
            }

            // Kitsu
            if (item.TryGetProviderId(Constants.KitsuProviderId, out var kitsuId))
            {
                yield return $"https://kitsu.app/anime/{kitsuId}";
            }

            // MyAnimeList
            if (item.TryGetProviderId(Constants.MalProviderId, out var malId))
            {
                yield return $"https://myanimelist.net/anime/{malId}";
            }
        }
    }
}
