using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AnimeMultiSource.Configuration
{
    public enum TitleFieldType
    {
        Title,
        TitleEnglish,
        TitleJapanese
    }

    public enum OriginalTitleFieldType
    {
        Title,
        TitleEnglish,
        TitleJapanese,
        TitleRomaji
    }

    public enum DataSourceType
    {
        AniDB,
        Anilist,
        AniSearch,
        Kitsu,
        Jikan
    }

    public enum RuntimeDataSourceType
    {
        Anilist,
        Jikan
    }

    public enum SeasonTitleFormatType
    {
        MetadataTitle,
        Numbered
    }

    public enum SeasonOverviewSourceType
    {
        Anilist,
        Jikan,
        PreferJikan
    }

    public enum AnimeScheduleModeType
    {
        Sub,
        Dub,
        Combined
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public string ExcludedPaths { get; set; } = string.Empty;

        /// <summary>
        /// Opt-in. The plugin only talks to the anime-lists API when the user has switched this on: the API is run by the plugin's
        /// author, and using it sends folder names, years and anime ids to that server. Off by default.
        /// </summary>
        public bool UseAnimeListsApi { get; set; }

        /// <summary>
        /// Base URL of the anime-lists API (title search + id cross-references). Not shown in the settings page: users do not need
        /// to change it, so it is always the author's server. It stays a stored value only so that someone hosting their own copy
        /// can point the plugin at it by editing the settings XML. Only used when <see cref="UseAnimeListsApi"/> is on.
        /// </summary>
        public string AnimeListsApiUrl { get; set; } = Constants.DefaultAnimeListsApiUrl;

        /// <summary>
        /// The URL the providers use: the configured one when the user opted in, otherwise empty - which every provider treats
        /// as "no API". Providers must read this, never <see cref="AnimeListsApiUrl"/>.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public string EffectiveAnimeListsApiUrl => UseAnimeListsApi && !string.IsNullOrWhiteSpace(AnimeListsApiUrl) ? AnimeListsApiUrl.Trim() : string.Empty;

        public PluginConfiguration()
        {
            AniDbRateLimit = 2000;

            // Title settings
            TitleField = TitleFieldType.Title;
            TitleDataSource = DataSourceType.AniDB;

            OriginalTitleField = OriginalTitleFieldType.TitleJapanese;
            OriginalTitleDataSource = DataSourceType.AniDB;

            // Runtime
            RuntimeDataSource = RuntimeDataSourceType.Anilist;

            // Genres
            ApprovedGenres =
@"Vampire
Thriller
Samurai
Suspense
Supernatural
Super Power
Sports
Space
Slice of Life
Shounen
Shoujo
Seinen
Sci-Fi
School
Romance
Reverse Harem
Psychological
Parody
Mystery
Music
Military
Mecha
Martial Arts
Mahou Shoujo
Mythology
Magic
Kids
Josei
Iyashikei
Isekai
Horror
Harem
Gore
Gourmet
Girls Love
Fantasy
Ecchi
Drama
Demons
Comedy
Boys Love
Avant Garde
Adventure
Action";

            // AniDB settings
            EnableAniDbTags = true;
            AniDbClientName = "mediabrowser";
            AniDbClientVersion = "1";

            SeasonTitleFormat = SeasonTitleFormatType.MetadataTitle;
            SeasonOverviewSource = SeasonOverviewSourceType.PreferJikan;

            // Fanart.tv
            FanartMaxBackdrops = 5;
            PersonalApiKey = string.Empty;
            FanartLanguages = string.Empty;
            FanartProbeImageSize = true;
            BackdropMinWidth = 1920;
            BackdropMinHeight = 1080;
            BackdropMinAspectRatio = 1.78;

            // AnimeSchedule.net
            AnimeScheduleApiKey = string.Empty;
            AnimeScheduleMode = AnimeScheduleModeType.Combined;
        }

        // Existing properties
        public int AniDbRateLimit { get; set; }

        // Title properties
        public TitleFieldType TitleField { get; set; }
        public DataSourceType TitleDataSource { get; set; }

        public OriginalTitleFieldType OriginalTitleField { get; set; }
        public DataSourceType OriginalTitleDataSource { get; set; }

        // Runtime
        public RuntimeDataSourceType RuntimeDataSource { get; set; }

        // Genres
        public string ApprovedGenres { get; set; }

        // AniDB properties
        public bool EnableAniDbTags { get; set; }
        public string AniDbClientName { get; set; }
        public string AniDbClientVersion { get; set; }

        // Season settings
        public SeasonTitleFormatType SeasonTitleFormat { get; set; }
        public SeasonOverviewSourceType SeasonOverviewSource { get; set; }

        // Fanart.tv
        public string PersonalApiKey { get; set; }
        public int FanartMaxBackdrops { get; set; }
        public string FanartLanguages { get; set; }
        public bool FanartProbeImageSize { get; set; }
        public int BackdropMinWidth { get; set; }
        public int BackdropMinHeight { get; set; }
        public double BackdropMinAspectRatio { get; set; }

        // AnimeSchedule.net
        public string AnimeScheduleApiKey { get; set; }
        public AnimeScheduleModeType AnimeScheduleMode { get; set; }

        // Level of the plugin's own log file (AnimeMultiSource_yyyy-MM-dd.log): false = Info, true = Debug.
        public bool DebugLogging { get; set; }
    }
}
