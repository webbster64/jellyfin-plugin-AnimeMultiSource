# Changelog

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
