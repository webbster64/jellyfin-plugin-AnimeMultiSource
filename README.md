# Anime Multi Source (Jellyfin plugin)

Remote anime metadata, tags, artwork and people for Jellyfin using multiple sources (AniList, AniDB, Jikan/MAL, TVDB, Fanart.tv, Fribb mappings and .plexmatch files). Built for large libraries with cautious rate limiting and persistent caching.

## Features
- Multi-source IDs: resolves AniList, AniDB, MAL, TVDB, TMDB, IMDb, Kitsu, AniSearch via Fribb mappings + optional `.plexmatch` overrides.
- Metadata: titles, descriptions, genres, studios, scores, relations/seasons, people (voice actors/staff), tags (AniDB).
- Images: fanart.tv + TVDB backdrops, posters, logos, season art with quality filters and limits.
- Rate limits & backoff: AniList (30/min), Jikan (spacing + retry-after), AniDB (soft cap with backoff and ban detection).
- Persistent caches: AniDB and AniList responses cached up to 5 days and persisted to disk to survive restarts.

## Requirements
- Jellyfin 10.11.2+ (net9.0 plugin, ABI 10.11.2.0)
- API keys (optional but recommended):
  - Fanart.tv personal API key
  - AniDB client name/version (for tags)

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

## Rate limits & caching
- AniList: spaced to ~30 req/min; cached 5 days; persisted on disk.
- AniDB: soft daily cap with slow mode; ban/limit responses trigger backoff; cached 5 days; persisted on disk.
- Jikan/MAL: spaced (~2.5s) with retry-after; lightweight caching via AniList reuse where possible.
- Persistent cache file: `provider-cache.json` under the plugin data folder (fallback to `AppContext.BaseDirectory/AnimeMultiSourceCache`). Entries older than 5 days are discarded automatically.

## Usage notes
- `.plexmatch` files are honored for ID hints (title/year/TVDB/IMDb).
- If AniDB is temporarily paused, tags will be skipped for that window but metadata will still complete; caching prevents repeat hits after the first successful fetch.
- Logs include rate-limit waits, cache hits, and any AniDB backoff reasons to help diagnose slowdowns.

## Troubleshooting
- No tags? Check logs for AniDB backoff messages; waits clear automatically. After a successful tag pull, results are cached/persisted for 5 days.
- Fanart/TVDB images missing? Verify keys and item has a TVDB ID.
- Slow first scan on huge libraries is expected; subsequent scans benefit from caches.

## License
GPLv3 (matches Jellyfin plugin requirements).
