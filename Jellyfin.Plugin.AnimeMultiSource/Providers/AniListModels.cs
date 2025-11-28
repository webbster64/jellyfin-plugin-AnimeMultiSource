using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class AniListResponse
    {
        [JsonPropertyName("data")]
        public AniListData? Data { get; set; }
    }

    public class AniListData
    {
        [JsonPropertyName("Media")]
        public AniListMedia? Media { get; set; }
    }

    public class AniListMedia
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public AniListTitle? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("genres")]
        public List<string>? Genres { get; set; }

        [JsonPropertyName("duration")]
        public int? Duration { get; set; }

        [JsonPropertyName("averageScore")]
        public int? AverageScore { get; set; }

        [JsonPropertyName("episodes")]
        public int? Episodes { get; set; }
        
        [JsonPropertyName("format")]
        public string? Format { get; set; }
        
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("startDate")]
        public AniListDate? StartDate { get; set; }

        [JsonPropertyName("endDate")]
        public AniListDate? EndDate { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("characters")]
        public AniListCharacterConnection? Characters { get; set; }
        
        [JsonPropertyName("staff")]
        public AniListStaffConnection? Staff { get; set; }

        [JsonPropertyName("relations")]
        public AniListRelationConnection? Relations { get; set; }

        [JsonPropertyName("idMal")]
        public int? IdMal { get; set; }
    }

    public class AniListTitle
    {
        [JsonPropertyName("romaji")]
        public string? Romaji { get; set; }

        [JsonPropertyName("english")]
        public string? English { get; set; }

        [JsonPropertyName("native")]
        public string? Native { get; set; }
    }

    public class AniListName
    {
        [JsonPropertyName("full")]
        public string? Full { get; set; }
        
        [JsonPropertyName("native")]
        public string? Native { get; set; }
    }

    public class AniListDate
    {
        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("month")]
        public int? Month { get; set; }

        [JsonPropertyName("day")]
        public int? Day { get; set; }
    }

    public class AniListCharacterConnection
    {
        [JsonPropertyName("edges")]
        public List<AniListCharacterEdge>? Edges { get; set; }
    }

    public class AniListCharacterEdge
    {
        [JsonPropertyName("node")]
        public AniListCharacter? Node { get; set; }
        
        [JsonPropertyName("role")]
        public string? Role { get; set; }
        
        [JsonPropertyName("voiceActors")]
        public List<AniListVoiceActor>? VoiceActors { get; set; }
    }

    public class AniListCharacter
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        
        [JsonPropertyName("name")]
        public AniListName? Name { get; set; }
        
        [JsonPropertyName("image")]
        public AniListImage? Image { get; set; }
    }

    public class AniListVoiceActor
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        
        [JsonPropertyName("name")]
        public AniListName? Name { get; set; }
        
        [JsonPropertyName("image")]
        public AniListImage? Image { get; set; }
        
        [JsonPropertyName("language")]
        public string? Language { get; set; }
    }

    public class AniListStaffConnection
    {
        [JsonPropertyName("edges")]
        public List<AniListStaffEdge>? Edges { get; set; }
    }

    public class AniListStaffEdge
    {
        [JsonPropertyName("node")]
        public AniListStaff? Node { get; set; }
        
        [JsonPropertyName("role")]
        public string? Role { get; set; }
    }

    public class AniListStaff
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        
        [JsonPropertyName("name")]
        public AniListName? Name { get; set; }
        
        [JsonPropertyName("image")]
        public AniListImage? Image { get; set; }
    }

    public class AniListImage
    {
        [JsonPropertyName("large")]
        public string? Large { get; set; }
        
        [JsonPropertyName("medium")]
        public string? Medium { get; set; }
    }

    public class AniListRelationConnection
    {
        [JsonPropertyName("edges")]
        public List<AniListRelationEdge>? Edges { get; set; }
    }

    public class AniListRelationEdge
    {
        [JsonPropertyName("relationType")]
        public string? RelationType { get; set; }

        [JsonPropertyName("node")]
        public AniListRelationNode? Node { get; set; }
    }

    public class AniListRelationNode
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("title")]
        public AniListTitle? Title { get; set; }

        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("episodes")]
        public int? Episodes { get; set; }

        [JsonPropertyName("season")]
        public string? Season { get; set; }

        [JsonPropertyName("seasonYear")]
        public int? SeasonYear { get; set; }
    }
}
