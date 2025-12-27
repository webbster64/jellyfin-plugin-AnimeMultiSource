# Changelog

## 1.0.3.0 - 2025-12-21
- Append an AMS rating label to the displayed rating line.

## 1.0.3.1 - 2025-12-22
- Aggregate AniDB tags across all mapped TV seasons for the series.
- Populate season tags from AniDB per season mapping (no genre fallback).

## 1.0.3.2 - 2025-12-22
- Allow ONA titles to use Jikan data (fixes missing rating/title for Japan Sinks: 2020).

## 1.0.3.3 - 2025-12-22
- Keep season-specific mappings when AniList root differs (prevents ID swaps on sequels like Treasure Chest of Courage).

## 1.0.3.4 - 2025-12-22
- Add mapping overrides for Dragon Ball entries and Ranking of Kings: Treasure Chest of Courage to prevent ID swaps.

## 1.0.3.5 - 2025-12-22
- When a title is in the override list, skip AniList root realignment entirely (keeps per-show IDs/names for overrides like Dragon Ball Daima).

## 1.0.3.6 - 2025-12-22
- Defer all Dragon Ball seasons to TVDB/other providers to avoid broken AniList chains and incorrect season names.

## 1.0.3.7 - 2025-12-27
- Version bump to publish the latest fixes.

## 1.0.2.2 - 2025-12-21
- Skip processing when .plexmatch is missing; log a clear banner and exit.

## 1.0.2.1 - 2025-12-18
- Avoid false-positive AniDB ban detection on valid XML responses.

## 1.0.2.0 - 2025-12-18
- Set assembly version to match plugin version.
- Use cached AniDB tags even when AniDB is rate-limited.

## 1.0.1.6 - 2025-12-18
- Use cached AniDB tags even when AniDB is rate-limited.

## 1.0.1.5 - 2025-12-18
- Fix season artwork lookups by prioritizing series TVDB IDs for seasons.

## 1.0.1.4 - 2025-12-18
- Guard against configuration load failures during multi-version plugin updates.

## 1.0.1.3 - 2025-12-18
- Fix external URL labels by splitting providers per service.
- Ensure season sort title is always set and enforce sort ordering.
- Set season index number for consistent season artwork lookups.
- Add Fanart.tv language tagging, optional language override, and size probing.

## 1.0.1.1 - 2025-11-29
- Prefer TV mappings over OVA duplicates in Fribb anime lists to avoid mis-linking (e.g., Future Diary now maps to the TV entry).

## 1.0.1.0 - 2025-11-29
- Add TVDB poster/banner/logo/clearart fallback when Fanart.tv has no series art.
- Reuse the TVDB series payload across season lookups to avoid duplicate API calls.
- Fix TVDB backdrop type handling so backgrounds are detected correctly.

## 1.0.0.0 - 2025-11-28
- Initial release targeting Jellyfin 10.11.2 ABI.
