# Anime Multi Source (Jellyfin plugin)

Remote anime metadata, tags, artwork and people for Jellyfin using multiple sources (AniList, AniDB, Jikan/MAL, TVDB, Fanart.tv, Fribb mappings and .plexmatch files). Built for large libraries with cautious rate limiting and persistent caching.

## Features
- Multi-source IDs: resolves AniList, AniDB, MAL, TVDB, TMDB, IMDb, Kitsu, AniSearch via Fribb mappings + optional `.plexmatch` overrides. Backfills gaps in Fribb's data from `manami-project/anime-offline-database` and (optionally) AnimeSchedule.net, for titles Fribb hasn't fully cross-referenced yet.
- Metadata: titles, descriptions, genres, studios, scores, relations/seasons, people (voice actors/staff), tags (AniDB).
- Images: fanart.tv + TVDB backdrops, posters, logos, season art with quality filters and limits.
- Upcoming episodes: optional AnimeSchedule.net integration populates Jellyfin's built-in "Upcoming" tab with real sub/dub-aware air dates. See [AnimeSchedule.net (Upcoming episodes)](#animeschedulenet-upcoming-episodes) below.
- Localized libraries: season folders named in other languages (e.g. French "Saison 1") resolve correctly; numbered season titles and TVDB episode translations follow Jellyfin's configured display language.
- Rate limits & backoff: AniList (30/min), Jikan (spacing + retry-after), AniDB (soft cap with backoff and ban detection).
- Persistent caches: AniDB and AniList responses cached up to 5 days and persisted to disk to survive restarts.

## Requirements
- Jellyfin 10.11.3+ (net9.0 plugin, ABI 10.11.3.0)
- API keys (all optional but recommended):
  - Fanart.tv personal API key (logos/backdrops)
  - AnimeSchedule.net API key (Upcoming episodes; get one from your AnimeSchedule.net account settings, API tab)

### .plexmatch files (strongly recommended)
- The plugin honors `.plexmatch` files to improve ID resolution. Supported fields: `title`, `year`, `tvdbid`, `imdbid`, and `anilistid`/`malid`.
- `anilistid`/`malid` let a series resolve directly off AniList/MAL even when Fribb hasn't cross-referenced it to a TVDB/IMDb id yet (common for brand-new simulcasts) - add one of these instead of `tvdbid`/`imdbid` for those titles.
- Sonarr can generate `.plexmatch` automatically: go to **Settings → Metadata**, enable **Plex**, and tick the option to write `.plexmatch` files.
- If you already have `.plexmatch` files in your library, keep them alongside the series folders—no further setup needed.

## Installation
**Option 1: Plugin repository (recommended)**
1) Jellyfin Dashboard → Plugins → Repositories.
2) Add repository: Name `AnimeMultiSource`, URL `https://raw.githubusercontent.com/webbster64/jellyfin-plugin-AnimeMultiSource/main/manifest.json`.
3) Go to Catalog, find **Anime Multi Source**, click Install.
4) Restart Jellyfin (then hard refresh browser: Ctrl+Shift+R / Cmd+Shift+R).

**Option 2: Manual install from release**
1) Download the latest `AnimeMultiSource_v*.zip` from the [releases](https://github.com/webbster64/jellyfin-plugin-AnimeMultiSource/releases).
2) Extract into your Jellyfin `plugins/AnimeMultiSource/` folder.
3) Restart Jellyfin.

**Option 3: Build from source**
1) `dotnet build` (or `dotnet publish -c Release`) in the repo root.
2) Copy the contents of `Jellyfin.Plugin.AnimeMultiSource/bin/<Configuration>/net9.0/` into your Jellyfin `plugins/AnimeMultiSource/` folder.
3) Restart Jellyfin.

## Configuration
Open **Dashboard -> Plugins -> Anime Multi Source**:
- Enter Fanart.tv personal key (for logos/backdrops).
- Set AniDB client name/version.
- Configure backdrop limits/quality and enable/disable sources as desired.
- Approved genres: prefilled with a curated list; edit or clear as needed (one genre per line).

### Metadata and image providers
- In your anime library settings, leave only **Anime Multi Source** enabled under both **Metadata downloaders** and **Image fetchers**. It replaces the other anime metadata and artwork providers, including TVDB and Fanart.
- Disable separate AniDB and AniList plugins to prevent them from adding duplicate provider IDs to your items.
- **Missing Episode Fetcher** can remain enabled. If it causes unexpected results, disable it and report the problem.

### AnimeSchedule.net (Upcoming episodes)
Optional. Adds mapped anime to Jellyfin's built-in **Upcoming** tab (`Shows → Upcoming`) with real air dates, and backstops title/overview/id resolution when Fribb/Jikan/AniList come up short for a title.

1) Get an API key from your AnimeSchedule.net account settings (API tab).
2) Enter it under **Dashboard → Plugins → Anime Multi Source → AnimeSchedule.net**, and choose a schedule: **Sub**, **Dub**, or **Combined** (prefers dub, falls back to sub per-episode if no dub exists).
3) Leave the key blank to disable the feature entirely - nothing else changes.

Upcoming episodes refresh whenever a series is refreshed, and on a scheduled task (**Dashboard → Scheduled Tasks → Anime Multi Source → Refresh AnimeSchedule Upcoming Episodes**, every 8 hours by default) so schedule delays/reschedules that happen between refreshes still get picked up. Episode titles show as `TBA` - AnimeSchedule is a scheduling calendar, not an episode database, so no title is ever fabricated.

## Rate limits & caching
- AniList: spaced to ~30 req/min; cached 5 days; persisted on disk.
- AniDB: soft daily cap with slow mode; ban/limit responses trigger backoff; cached 5 days; persisted on disk.
- Jikan/MAL: spaced (~2.5s) with retry-after; lightweight caching via AniList reuse where possible.
- Persistent cache file: `provider-cache.json` under the plugin data folder (fallback to `AppContext.BaseDirectory/AnimeMultiSourceCache`). Entries older than 5 days are discarded automatically.

## Usage notes
- `.plexmatch` files are honored for ID hints (title/year/TVDB/IMDb).
- If AniDB is temporarily paused, tags will be skipped for that window but metadata will still complete; caching prevents repeat hits after the first successful fetch.
- Logs include rate-limit waits, cache hits, and any AniDB backoff reasons to help diagnose slowdowns.

### File and folder naming
- Use standard season/episode naming: `Show Name/Season 1/Show Name - S01E12 - Title.mkv`. Avoid date/daily formats (e.g., `Show Name - 2022-03-23.mkv`) because Jellyfin/TVDB episode mapping will be skipped.
- Keep one series per folder with season subfolders (`Season 1`, `Season 2`, …). Place `.plexmatch` in the series root.
- If using Sonarr/Radarr, set the series type to “Standard” (not “Daily/Date”) so files are named with `SxxEyy`.

## Troubleshooting
- No tags? Check logs for AniDB backoff messages; waits clear automatically. After a successful tag pull, results are cached/persisted for 5 days.
- Fanart/TVDB images missing? Verify keys and item has a TVDB ID.
- Slow first scan on huge libraries is expected; subsequent scans benefit from caches.

## License
GPLv3 (matches Jellyfin plugin requirements).
