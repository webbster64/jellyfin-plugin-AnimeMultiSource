using System.Collections.Generic;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    [XmlRoot(ElementName = "anime")]
    public class AniDbAnime
    {
        [XmlElement(ElementName = "titles")]
        public AniDbTitles? Titles { get; set; }

        [XmlElement(ElementName = "tags")]
        public AniDbTags? Tags { get; set; }
    }

    [XmlRoot(ElementName = "titles")]
    public class AniDbTitles
    {
        [XmlElement(ElementName = "title")]
        public List<AniDbTitle>? TitleList { get; set; }
    }

    [XmlRoot(ElementName = "title")]
    public class AniDbTitle
    {
        [XmlAttribute(AttributeName = "type")]
        public string? Type { get; set; }

        [XmlAttribute(AttributeName = "language")]
        public string? Language { get; set; }

        [XmlText]
        public string? Value { get; set; }
    }

    [XmlRoot(ElementName = "tags")]
    public class AniDbTags
    {
        [XmlElement(ElementName = "tag")]
        public List<AniDbTag>? TagList { get; set; }
    }

    [XmlRoot(ElementName = "tag")]
    public class AniDbTag
    {
        [XmlAttribute(AttributeName = "id")]
        public int Id { get; set; }

        [XmlAttribute(AttributeName = "weight")]
        public int Weight { get; set; }

        [XmlElement(ElementName = "name")]
        public string? Name { get; set; }

        [XmlElement(ElementName = "description")]
        public string? Description { get; set; }

        [XmlElement(ElementName = "spoiler")]
        public bool Spoiler { get; set; }
    }
}