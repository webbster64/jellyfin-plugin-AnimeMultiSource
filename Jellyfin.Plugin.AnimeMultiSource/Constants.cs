namespace Jellyfin.Plugin.AnimeMultiSource
{
    public static class Constants
    {
        public const string PluginName = "AnimeMultiSource";
        public const string PluginGuid = "8eca6f17-71fe-4309-a670-3cae083f22bd";

        /// <summary>Tag added to every show and movie this plugin updates, when the user turns it on.</summary>
        public const string BadgeTag = "Anime Multi Source";

        /// <summary>The anime-lists API run by the plugin author; pre-filled in the settings but never used until the user opts in.</summary>
        public const string DefaultAnimeListsApiUrl = "https://p01--anime-lists--zq6lfhsvfj9n.code.run";
        public const string TvdbProjectApiKey = "7f7eed88-2530-4f84-8ee7-f154471b8f87";

        // Provider IDs (must match what we use in SetProviderId and IExternalId)
        public const string AniDbProviderId = "AniDB";
        public const string AniListProviderId = "AniList";
        public const string MalProviderId = "Mal";
        public const string KitsuProviderId = "Kitsu";
        public const string AniSearchProviderId = "AniSearch";

        // URLs
        public const string FribbAnimeListsUrl = "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-full.json";
        public const string AnimeOfflineDatabaseUrl = "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-offline-database-reduced.json";

        // File names
        public const string PlexMatchFileName = ".plexmatch";

        // Cache durations
        public const int AnimeListCacheHours = 6;
    }
}
