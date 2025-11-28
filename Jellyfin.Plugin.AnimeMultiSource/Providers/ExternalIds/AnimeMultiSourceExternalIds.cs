using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers.ExternalIds
{
    // AniDB External ID
    public class AniDbExternalId : IExternalId
    {
        public bool Supports(IHasProviderIds item)
            => item is Series || item is Movie;

        public string ProviderName => "AniDB";

        public string Key => Constants.AniDbProviderId;

        public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        public string? UrlFormatString => "https://anidb.net/anime/{0}";
    }

    // AniList External ID
    public class AniListExternalId : IExternalId
    {
        public bool Supports(IHasProviderIds item)
            => item is Series || item is Movie;

        public string ProviderName => "AniList";

        public string Key => Constants.AniListProviderId;

        public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        public string? UrlFormatString => "https://anilist.co/anime/{0}";
    }

    // AniSearch External ID
    public class AniSearchExternalId : IExternalId
    {
        public bool Supports(IHasProviderIds item)
            => item is Series || item is Movie;

        public string ProviderName => "AniSearch";

        public string Key => Constants.AniSearchProviderId;

        public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        public string? UrlFormatString => "https://www.anisearch.com/anime/{0}";
    }

    // Kitsu External ID
    public class KitsuExternalId : IExternalId
    {
        public bool Supports(IHasProviderIds item)
            => item is Series || item is Movie;

        public string ProviderName => "Kitsu";

        public string Key => Constants.KitsuProviderId;

        public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        public string? UrlFormatString => "https://kitsu.app/anime/{0}";
    }

    // MyAnimeList External ID
    public class MalExternalId : IExternalId
    {
        public bool Supports(IHasProviderIds item)
            => item is Series || item is Movie;

        public string ProviderName => "MyAnimeList";

        public string Key => Constants.MalProviderId;

        public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        public string? UrlFormatString => "https://myanimelist.net/anime/{0}";
    }
}
