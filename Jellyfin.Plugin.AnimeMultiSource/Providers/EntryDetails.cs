using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource.Providers
{
    /// <summary>
    /// What one anime entry contributes to a card: parental rating, genres, studios, people and episode runtime. The show
    /// card uses its first season's entry, a season card uses that season's own entry (Maria Holic season 2 = "Maria Holic
    /// Alive"), and an episode takes its rating from its season's entry.
    /// </summary>
    public sealed class EntryDetails
    {
        public JikanAnime? Jikan { get; set; }

        public AniListMedia? AniList { get; set; }

        /// <summary>Jikan's rating; Kitsu's (written the same way) when Jikan is down or has none.</summary>
        public string? ParentalRating { get; set; }

        /// <summary>Jikan's and AniList's genres, themes and demographics, limited to the approved list when one is set.</summary>
        public List<string> Genres { get; set; } = new();

        public List<string> Studios { get; set; } = new();

        public List<PersonInfo> People { get; set; } = new();

        /// <summary>Minutes per episode: Jikan's duration or AniList's, in the order the settings ask for, the other as a fallback.</summary>
        public int? RuntimeMinutes { get; set; }
    }

    public sealed class EntryDetailsLoader
    {
        private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Rating)> RatingCache = new();
        private static readonly TimeSpan RatingCacheDuration = TimeSpan.FromHours(6);
        private static readonly TimeSpan RatingMissCacheDuration = TimeSpan.FromMinutes(10);

        private readonly ApiService _apiService;
        private readonly ILogger _logger;

        public EntryDetailsLoader(ApiService apiService, ILogger logger)
        {
            _apiService = apiService;
            _logger = logger;
        }

        /// <summary>The genres the user approved in the settings (one per line); empty means "all".</summary>
        public static List<string> ApprovedGenres(PluginConfiguration config)
        {
            if (string.IsNullOrEmpty(config.ApprovedGenres))
            {
                return new List<string>();
            }

            return config.ApprovedGenres
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim())
                .Where(g => !string.IsNullOrEmpty(g))
                .ToList();
        }

        /// <summary>
        /// One entry per person. The raw staff list repeats a person once per role (a composer is also the lyricist and the
        /// arranger; a voice actor plays two characters), and Jellyfin identifies a person by name alone, so the repeats make
        /// saving the cast of a season fail ("UNIQUE constraint failed: Peoples.Id"). The roles are joined instead.
        /// </summary>
        public static List<PersonInfo> MergePeopleByName(IEnumerable<PersonInfo> people)
        {
            var merged = new List<PersonInfo>();
            var roles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var seen = new Dictionary<string, PersonInfo>(StringComparer.Ordinal);

            foreach (var person in people)
            {
                if (string.IsNullOrWhiteSpace(person.Name))
                {
                    continue;
                }

                var key = FoldName(person.Name);
                if (!seen.TryGetValue(key, out var first))
                {
                    // A copy: the people lists are cached and shared by the show, its seasons and the movies, so changing
                    // the original would pile the roles up again on every refresh.
                    first = ClonePerson(person);
                    seen[key] = first;
                    roles[key] = new List<string>();
                    merged.Add(first);
                }
                else if (string.IsNullOrWhiteSpace(first.ImageUrl) && !string.IsNullOrWhiteSpace(person.ImageUrl))
                {
                    first.ImageUrl = person.ImageUrl;
                }

                if (!string.IsNullOrWhiteSpace(person.Role) && !roles[key].Contains(person.Role, StringComparer.OrdinalIgnoreCase))
                {
                    roles[key].Add(person.Role);
                }
            }

            foreach (var person in merged)
            {
                var list = roles[FoldName(person.Name)];
                if (list.Count > 1)
                {
                    person.Role = string.Join(", ", list);
                }
            }

            return merged;
        }

        /// <summary>
        /// A copy of a person WITHOUT the database id. Jellyfin writes the id of the person it saved back onto the object it
        /// was given; our people lists are cached and shared by the show, its seasons and the movies, so reusing the same
        /// objects hands the next save a stale id. If that save also changes the person (a different name, a merged
        /// entry) the id no longer fits the row and the whole cast fails with "UNIQUE constraint failed: Peoples.Id".
        /// </summary>
        public static PersonInfo ClonePerson(PersonInfo person)
        {
            var copy = new PersonInfo
            {
                Name = person.Name,
                Role = person.Role,
                Type = person.Type,
                ImageUrl = person.ImageUrl
            };

            foreach (var providerId in person.ProviderIds)
            {
                copy.ProviderIds[providerId.Key] = providerId.Value;
            }

            return copy;
        }

        private static string FoldName(string name)
        {
            var decomposed = name.Trim().Normalize(System.Text.NormalizationForm.FormD);
            var builder = new System.Text.StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }

            return builder.ToString();
        }

        public static string? CleanRating(string? rating) =>
            string.IsNullOrWhiteSpace(rating) ? null : rating.Trim();

        public static DateTime? ToDate(AniListDate? date)
        {
            if (date?.Year is not int year || year < 1 || year > 9999)
            {
                return null;
            }

            var month = Math.Clamp(date.Month ?? 1, 1, 12);
            var day = Math.Clamp(date.Day ?? 1, 1, DateTime.DaysInMonth(year, month));
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }

        /// <summary>
        /// Minutes per episode. Jikan's "24 min per ep" or AniList's duration, whichever the settings prefer, and the other
        /// one when the preferred source has nothing (Jikan is often unreachable, so the fallback matters).
        /// </summary>
        public int? PickRuntime(JikanAnime? jikan, AniListMedia? aniList, RuntimeDataSourceType preferred)
        {
            int? fromJikan = int.TryParse(_apiService.ParseJikanDuration(jikan?.Duration), out var minutes) && minutes > 0 ? minutes : null;
            int? fromAniList = aniList?.Duration is > 0 ? aniList.Duration : null;

            return preferred == RuntimeDataSourceType.Anilist
                ? fromAniList ?? fromJikan
                : fromJikan ?? fromAniList;
        }

        /// <summary>Combines already fetched data; no network access.</summary>
        public EntryDetails Build(JikanAnime? jikan, AniListMedia? aniList, string? kitsuRating, PluginConfiguration config)
        {
            return new EntryDetails
            {
                Jikan = jikan,
                AniList = aniList,
                ParentalRating = CleanRating(jikan?.Rating) ?? CleanRating(kitsuRating),
                Genres = _apiService.CombineGenres(jikan, aniList, ApprovedGenres(config)),
                Studios = _apiService.GetStudios(jikan, aniList),
                RuntimeMinutes = PickRuntime(jikan, aniList, config.RuntimeDataSource)
            };
        }

        public async Task<EntryDetails> LoadAsync(PluginConfiguration config, long? aniListId, long? malId, long? kitsuId, bool includePeople)
        {
            var aniList = aniListId.HasValue ? await _apiService.GetAniListAnimeAsync(aniListId.Value).ConfigureAwait(false) : null;
            long? mal = malId ?? aniList?.IdMal;
            var jikan = mal.HasValue ? await _apiService.GetJikanAnimeAsync(mal.Value).ConfigureAwait(false) : null;

            // Only ask Kitsu when Jikan did not give a rating.
            var kitsuRating = CleanRating(jikan?.Rating) == null
                ? await _apiService.GetKitsuParentalRatingAsync(kitsuId).ConfigureAwait(false)
                : null;

            var details = Build(jikan, aniList, kitsuRating, config);

            if (includePeople && (aniListId.HasValue || mal.HasValue))
            {
                try
                {
                    details.People = MergePeopleByName(await _apiService.GetPeopleAsync(aniListId ?? 0, mal ?? 0).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching people for AniList {AniListId} / MAL {MalId}", aniListId, mal);
                }
            }

            return details;
        }

        /// <summary>
        /// Just the parental rating of an entry (Jikan, then Kitsu), cached: an episode asks for it once per episode and a
        /// whole season shares one answer. A miss is cached only briefly so a Jikan outage does not stick.
        /// </summary>
        public async Task<string?> GetParentalRatingAsync(long? aniListId, long? malId, long? kitsuId)
        {
            if (!aniListId.HasValue && !malId.HasValue && !kitsuId.HasValue)
            {
                return null;
            }

            var key = $"{aniListId}|{malId}|{kitsuId}";
            if (RatingCache.TryGetValue(key, out var cached)
                && DateTimeOffset.UtcNow - cached.At < (cached.Rating != null ? RatingCacheDuration : RatingMissCacheDuration))
            {
                return cached.Rating;
            }

            long? mal = malId;
            if (!mal.HasValue && aniListId.HasValue)
            {
                mal = (await _apiService.GetAniListAnimeAsync(aniListId.Value).ConfigureAwait(false))?.IdMal;
            }

            var jikan = mal.HasValue ? await _apiService.GetJikanAnimeAsync(mal.Value).ConfigureAwait(false) : null;
            var rating = CleanRating(jikan?.Rating) ?? CleanRating(await _apiService.GetKitsuParentalRatingAsync(kitsuId).ConfigureAwait(false));

            RatingCache[key] = (DateTimeOffset.UtcNow, rating);
            return rating;
        }
    }
}
