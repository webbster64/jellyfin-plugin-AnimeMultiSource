using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class TvdbLoginResponse
    {
        public string? Status { get; set; }
        public TvdbLoginData? Data { get; set; }
    }

    public class TvdbLoginData
    {
        public string? Token { get; set; }
    }

    public class TvdbEpisodeListResponse
    {
        public string? Status { get; set; }
        public TvdbEpisodeListData? Data { get; set; }
        public TvdbLinks? Links { get; set; }
    }

    public class TvdbEpisodeListData
    {
        public TvdbSeriesSummary? Series { get; set; }
        public List<TvdbEpisode> Episodes { get; set; } = new();
    }

    public class TvdbSeriesSummary
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Slug { get; set; }
    }

    public class TvdbEpisode
    {
        public int Id { get; set; }
        public int SeriesId { get; set; }
        public string? Name { get; set; }
        public string? Aired { get; set; }
        public int? Runtime { get; set; }
        public List<string> NameTranslations { get; set; } = new();
        public string? Overview { get; set; }
        public List<string> OverviewTranslations { get; set; } = new();
        public string? Image { get; set; }
        public int? ImageType { get; set; }
        public int? IsMovie { get; set; }
        public int? Number { get; set; }
        public int? AbsoluteNumber { get; set; }
        public int? SeasonNumber { get; set; }
        public string? SeasonName { get; set; }
        public string? FinaleType { get; set; }
        public int? AirsBeforeSeason { get; set; }
        public int? AirsBeforeEpisode { get; set; }
        public string? Year { get; set; }
        public string? LastUpdated { get; set; }
    }

    public class TvdbLinks
    {
        public string? Prev { get; set; }
        public string? Self { get; set; }
        public string? Next { get; set; }

        [JsonPropertyName("total_items")]
        public int? TotalItems { get; set; }

        [JsonPropertyName("page_size")]
        public int? PageSize { get; set; }
    }

    public class TvdbTranslationResponse
    {
        public string? Status { get; set; }
        public TvdbTranslation? Data { get; set; }
    }

    public class TvdbTranslation
    {
        public string? Name { get; set; }
        public string? Overview { get; set; }
        public string? Language { get; set; }
    }

    public class TvdbEpisodeResponse
    {
        public string? Status { get; set; }
        public TvdbEpisode? Data { get; set; }
    }

    public class TvdbSeriesExtendedResponse
    {
        public string? Status { get; set; }
        public TvdbSeriesExtended? Data { get; set; }
    }

    public class TvdbSeriesExtended
    {
        public int Id { get; set; }
        public List<TvdbArtwork> Artworks { get; set; } = new();
        public List<TvdbSeason> Seasons { get; set; } = new();
    }

    public class TvdbSeason
    {
        public int Id { get; set; }
        public int SeriesId { get; set; }
        public TvdbSeasonType? Type { get; set; }
        public int? Number { get; set; }
        public string? Image { get; set; }
        public int? ImageType { get; set; }
    }

    public class TvdbSeasonType
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
    }

    public class TvdbArtwork
    {
        public int Id { get; set; }
        public string? Image { get; set; }
        public string? Thumbnail { get; set; }
        public string? Language { get; set; }
        public int? Type { get; set; }
        public int? Score { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public bool? IncludesText { get; set; }
        public int? SeasonId { get; set; }
    }
}
