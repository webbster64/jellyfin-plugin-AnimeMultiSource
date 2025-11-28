using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class FanartTvResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("thetvdb_id")]
        public string? TvdbId { get; set; }

        [JsonPropertyName("tvposter")]
        public List<FanartImage> TvPosters { get; set; } = new();

        [JsonPropertyName("clearart")]
        public List<FanartImage> ClearArt { get; set; } = new();

        [JsonPropertyName("showbackground")]
        public List<FanartImage> ShowBackgrounds { get; set; } = new();

        [JsonPropertyName("tvbanner")]
        public List<FanartImage> TvBanners { get; set; } = new();

        [JsonPropertyName("hdtvlogo")]
        public List<FanartImage> HdTvLogos { get; set; } = new();

        [JsonPropertyName("clearlogo")]
        public List<FanartImage> ClearLogos { get; set; } = new();

        [JsonPropertyName("tvthumb")]
        public List<FanartImage> TvThumbs { get; set; } = new();

        [JsonPropertyName("seasonposter")]
        public List<FanartSeasonImage> SeasonPosters { get; set; } = new();

        [JsonPropertyName("seasonbanner")]
        public List<FanartSeasonImage> SeasonBanners { get; set; } = new();

        [JsonPropertyName("seasonthumb")]
        public List<FanartSeasonImage> SeasonThumbs { get; set; } = new();
    }

    public class FanartImage
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("lang")]
        public string? Language { get; set; }
    }

    public class FanartSeasonImage : FanartImage
    {
        [JsonPropertyName("season")]
        public string? Season { get; set; }
    }
}
