# Migration and rollback

OneArr is designed to run **beside** your existing Sonarr, Radarr, Prowlarr and qBittorrent. Nothing in this document tells OneArr to change those applications.

## What OneArr does and does not do with legacy apps

- **Reads only.** The Sonarr/Radarr reader issues `GET` requests to `/api/v3/series` (Sonarr) or `/api/v3/movie` (Radarr). The smoke test asserts that no other method is used.
- **Never writes** to the legacy database, files, configuration or queue, and never disables their monitoring.
- **API keys are not stored.** You supply the legacy key for each preview; it is used only for that request.
- **Copies nothing automatically.** The preview lists entries and per-item errors (for example a legacy record without a TMDb id), then waits for you to select items and confirm.
- Only catalog metadata (title, year, TMDb id, monitored flag, root folder you choose) is copied into OneArr's `onearr.db`. Existing file paths are not moved, and indexer credentials or download ownership are not migrated.

## Before you start (you do this, not OneArr)

1. Back up each legacy app with its own built-in backup feature (Sonarr and Radarr: *System → Backup*). Store the archive outside OneArr's `/config`.
2. Note the qBittorrent categories the old apps use. OneArr uses `onearr-movies` and `onearr-tv` by default, so its torrents never share a category with the old apps.
3. Use a small synthetic `/data` tree for the first run (`torrents/` and `media/` with a few test files), as in `docker-compose.yml`.

## Migration steps

1. Start OneArr, complete first-run setup, and add your TMDb key in Settings.
2. Settings: set the torrent root (for example `/data/torrents`) and media root (`/data/media`). Keep **dry-run imports** enabled.
3. Migration Preview: choose Radarr or Sonarr, enter the base URL and API key, then *Load preview*.
4. Review the list and the per-item errors. Untick anything you do not want.
5. Choose a quality profile and a root folder inside the media root (for example `/data/media/movies`), then *Import selected* and confirm.
6. Check Movies and Series. Re-adding an existing title reports `exists` and creates nothing.

## Verifying before any cutover

- Compare counts and monitored flags for the selected titles with the legacy app.
- Run Queue → *Import dry run* and read the planned actions. Enable real imports only after this matches expectations.
- Keep the legacy apps running and monitoring until a real observation period has passed. Cutover is a separate, manual decision and is not automated.

## Rollback

OneArr's changes are additive to the legacy estate, so rollback is:

1. Stop OneArr (`docker compose stop onearr`, or stop the `dotnet` process). The legacy apps were never modified, so they keep working as before.
2. To drop OneArr's catalog, delete individual titles in OneArr or stop using it. Deleting `./config/onearr.db` discards **all OneArr state**, including job history and import records; do this only deliberately.
3. Files that OneArr hardlinked or copied into the media root are ordinary files. Remove them only with explicit intent. OneArr never deletes source files, and it never deletes seeding files in qBittorrent.
4. For torrents OneArr added, use qBittorrent's own UI to pause or remove them if you no longer want them. OneArr does not remove torrents.

## Known limits in this version

- No automatic grabbing or RSS monitoring. Searches run only when you press *Releases*.
- No season packs, specials, anime absolute numbering or subtitles.
- Series migration needs a TMDb id on the legacy record; entries without one are reported and skipped.
- Import places each completed file into `<root folder>/<title (year)>` for movies or `<root folder>/<title>/Season NN` for series. Files are not renamed.
