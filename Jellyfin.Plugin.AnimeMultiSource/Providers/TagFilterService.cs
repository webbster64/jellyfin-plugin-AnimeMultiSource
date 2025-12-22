using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    public class TagFilterService
    {
        private static readonly HashSet<string> ExcludedTagCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Japanese production",
            "adapted into other media",
            "adapted into Japanese movie",
            "character related tags which need deleting or merging",
            "ending tags that need merging",
            "human - non-human relationship",
            "put right what once went wrong",
            "tropes",
            "cast",
            "ending",
            "origin",
            "technical aspects",
            "Weekly Shounen Jump",
            "complete manga adaptation",
            "unsorted",
            "manga",
            "incomplete story",
            "place",
            "present",
            "plot continuity",
            "elements",
            "time",
            "setting",
            "original work",
            "themes",
            "target audience",
            "dynamic",
            "shoujo",
            "comedy",
            "seinen",
            "content indicators",
            "novel",
            "action",
            "ecchi",
            "romance",
            "harem",
            "fantasy",
            "contemporary fantasy",
            "storytelling",
            "speculative fiction",
            "TO BE MOVED TO CHARACTER",
            "TO BE MOVED TO EPISODE",
            "open-ended",
            "parody",
            "remastered version available",
            "thick line animation",
            "TV censoring",
            "wafuku -- TO BE SPLIT AND DELETED",
            "excessive censoring",
            "preaired episodes",
            "censored uncensored version",
            "season",
            "shounen",
            "multiple protagonists - TO BE MOVED TO PARENT OR DELETED",
            "school festival - TO BE SPLIT AND DELETED",
            "uniform -- TO BE SPLIT AND DELETED",
            "gun - TO BE SPLIT AND DELETED",
            "unusual weapons -- TO BE SPLIT AND DELETED",
            "RPG aspects",
            "medieval -- TO BE SPLIT AND DELETED",
            "maintenance tags"
        };

        public List<string> FilterTags(List<string> tags)
        {
            if (tags == null || !tags.Any())
                return new List<string>();

            return tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Where(tag => !IsExcluded(tag))
                .Select(tag => tag.Trim())
                .Distinct()
                .ToList();
        }

        private bool IsExcluded(string tag)
        {
            // Check if the tag exactly matches an excluded category
            if (ExcludedTagCategories.Contains(tag))
                return true;

            // Check if the tag contains any excluded category as a substring
            // This handles cases where tags might be variations of excluded categories
            foreach (var excluded in ExcludedTagCategories)
            {
                if (tag.IndexOf(excluded, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        public void LogFilteredTags(ILogger logger, List<string> originalTags, List<string> filteredTags)
        {
            if (originalTags == null || filteredTags == null)
                return;

            var removedCount = originalTags.Count - filteredTags.Count;
            if (removedCount > 0)
            {
                var removedTags = originalTags.Except(filteredTags, StringComparer.OrdinalIgnoreCase).ToList();
                logger.LogDebug("Filtered out {RemovedCount} tags: {RemovedTags}", removedCount, string.Join(", ", removedTags));
            }

            logger.LogDebug("Kept {KeptCount} tags after filtering", filteredTags.Count);
        }
    }
}
