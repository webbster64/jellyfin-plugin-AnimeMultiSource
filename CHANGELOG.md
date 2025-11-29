# Changelog

## 1.0.1.1 - 2025-11-29
- Prefer TV mappings over OVA duplicates in Fribb anime lists to avoid mis-linking (e.g., Future Diary now maps to the TV entry).

## 1.0.1.0 - 2025-11-29
- Add TVDB poster/banner/logo/clearart fallback when Fanart.tv has no series art.
- Reuse the TVDB series payload across season lookups to avoid duplicate API calls.
- Fix TVDB backdrop type handling so backgrounds are detected correctly.

## 1.0.0.0 - 2025-11-28
- Initial release targeting Jellyfin 10.11.2 ABI.
