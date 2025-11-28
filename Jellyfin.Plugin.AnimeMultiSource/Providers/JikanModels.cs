// [file name]: JikanModels.cs
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class JikanResponse
    {
        [JsonPropertyName("data")]
        public JikanAnime? Data { get; set; }
    }

    public class JikanAnime
    {
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("title_english")]
        public string? TitleEnglish { get; set; }

        [JsonPropertyName("title_japanese")]
        public string? TitleJapanese { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("aired")]
        public JikanAired? Aired { get; set; }

        [JsonPropertyName("rating")]
        public string? Rating { get; set; }

        [JsonPropertyName("score")]
        public double? Score { get; set; }

        [JsonPropertyName("scored_by")]
        public int? ScoredBy { get; set; }

        [JsonPropertyName("synopsis")]
        public string? Synopsis { get; set; }

        [JsonPropertyName("background")]
        public string? Background { get; set; }

        [JsonPropertyName("season")]
        public string? Season { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("broadcast")]
        public JikanBroadcast? Broadcast { get; set; }

        [JsonPropertyName("duration")]
        public string? Duration { get; set; }

        [JsonPropertyName("genres")]
        public List<JikanGenre>? Genres { get; set; }

        [JsonPropertyName("explicit_genres")]
        public List<JikanGenre>? ExplicitGenres { get; set; }

        [JsonPropertyName("themes")]
        public List<JikanGenre>? Themes { get; set; }

        [JsonPropertyName("demographics")]
        public List<JikanGenre>? Demographics { get; set; }

        [JsonPropertyName("studios")]
        public List<JikanStudio>? Studios { get; set; }
    }

    public class JikanAired
    {
        [JsonPropertyName("from")]
        public DateTime? From { get; set; }

        [JsonPropertyName("to")]
        public DateTime? To { get; set; }

        [JsonPropertyName("prop")]
        public JikanAiredProp? Prop { get; set; }
    }

    public class JikanAiredProp
    {
        [JsonPropertyName("from")]
        public JikanDate? From { get; set; }

        [JsonPropertyName("to")]
        public JikanDate? To { get; set; }
    }

    public class JikanDate
    {
        [JsonPropertyName("day")]
        public int? Day { get; set; }

        [JsonPropertyName("month")]
        public int? Month { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }
    }

    public class JikanBroadcast
    {
        [JsonPropertyName("day")]
        public string? Day { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }

        [JsonPropertyName("timezone")]
        public string? Timezone { get; set; }

        [JsonPropertyName("string")]
        public string? String { get; set; }
    }

    public class JikanGenre
    {
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    public class JikanStudio
    {
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    // Add to JikanModels.cs
    public class JikanCharactersResponse
    {
        [JsonPropertyName("data")]
        public List<JikanCharacter>? Data { get; set; }
    }

    public class JikanCharacter
    {
        [JsonPropertyName("character")]
        public JikanCharacterInfo? Character { get; set; }
        
        [JsonPropertyName("voice_actors")]
        public List<JikanVoiceActor>? VoiceActors { get; set; }
    }

    public class JikanCharacterInfo
    {
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }
        
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        
        [JsonPropertyName("images")]
        public JikanCharacterImages? Images { get; set; }
    }

    public class JikanCharacterImages
    {
        [JsonPropertyName("webp")]
        public JikanImage? WebP { get; set; }
    }

    public class JikanVoiceActor
    {
        [JsonPropertyName("person")]
        public JikanPerson? Person { get; set; }
    }

    public class JikanPerson
    {
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }
        
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        
        [JsonPropertyName("images")]
        public JikanPersonImages? Images { get; set; }
    }

    public class JikanPersonImages
    {
        [JsonPropertyName("webp")]
        public JikanImage? WebP { get; set; }
    }

    public class JikanImage
    {
        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; set; }
    }

}