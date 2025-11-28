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
        TitleJapanese
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

    public class PluginConfiguration : BasePluginConfiguration
    {
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
            BackdropMinWidth = 1920;
            BackdropMinHeight = 1080;
            BackdropMinAspectRatio = 1.78;
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
        public int BackdropMinWidth { get; set; }
        public int BackdropMinHeight { get; set; }
        public double BackdropMinAspectRatio { get; set; }
    }
}
