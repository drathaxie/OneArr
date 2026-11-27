# OneArr status and roadmap

Updated 2026-11-27: `main` is v0.7.9, and branch `tv-movie-gaps` holds the work listed as **branch**.

"Done" means on `main` and covered by tests. "Branch" means on `tv-movie-gaps`, covered by tests, not yet released.
Nothing on the branch has been tried on the Windows PC with the real library, indexers or qBittorrent yet.

## Where we are

### Catalog and metadata

| Item | Status |
|---|---|
| TMDb search and add, movies and series | Done |
| Fix match, manual and scheduled metadata refresh | Done (v0.7.9) |
| Exact air times from TVmaze | Done (v0.7.9) |
| Specials (season 0) unmonitored by default, with their own monitor toggle | Done; labelled "Specials" on the branch |
| Movie release dates (cinema, digital, disc); automatic search waits for digital or disc | Branch |
| Alternate titles (TMDb), accent folding, "&" = "and" | Branch |
| Anime: absolute episode numbers, per-series "anime numbering" switch | Branch |
| Import from a TMDb list or IMDb ids (preview, then add chosen titles) | Branch |
| Trakt lists | Not done: needs a Trakt app key |
| Scheduled list sync (auto-add new list entries) | Not done, by choice: imports are one-off |

### Library

| Item | Status |
|---|---|
| Reconcile existing files (preview, typed confirm, apply) | Done |
| Automation pauses until the catalog is reviewed | Done |
| Extras skipped automatically (Featurettes, Trailers, … folders and suffixes) | Branch |
| Ignore list for files and folders | Branch |
| Anime files in a library scan ("Show - 05.mkv") | Not done: only downloads are matched by absolute number |

### Search and grab

| Item | Status |
|---|---|
| Wanted search, new episodes first, hourly retries after airing | Done (v0.7.9) |
| RSS sync, opt-in per indexer | Done (v0.7.9) |
| Blocklist (manual, plus dead torrents after 24 h without progress) | Branch |
| PROPER / REPACK / vN recognised and preferred | Branch |
| Quality profiles: allowed sources, size limits, required/banned/preferred words | Branch |
| Upgrades: opt-in per profile, old file moved to a recycle bin on the same drive | Branch |

### Downloads and import

| Item | Status |
|---|---|
| qBittorrent, multiple clients, path mapping, automatic import (opt-in) | Done |
| Subtitles imported with their video, named after it | Branch |
| Upgrade replacement through the recycle bin, rolled back if the import fails | Branch |

### Operations

| Item | Status |
|---|---|
| Windows tray, self-install, updates and rollback, CI, release workflow | Done |
| Logs & Diagnostics page, and a diagnostics download without secrets | Branch |
| Calendar page | Branch |
| Notifications to a Discord webhook or any JSON endpoint | Branch |

## Defaults on the branch, after updating

Everything new that changes files or downloads is off until you switch it on:

- **Upgrades:** off for every profile. Turn on "upgrade existing files" in a profile *and* set a recycle bin folder under
  Import paths.
- **Anime numbering:** off for every series.
- **Notifications:** off until a webhook is saved.
- **Blocklist:** releases are only blocklisted when you press Blocklist, or when automatic grabbing is on and a torrent
  has had no progress 24 hours after the grab. The torrent itself is never removed.
- **Movie release dates and alternate titles:** filled in by the scheduled refresh, which now covers monitored movies as
  well as series (10 items every 5 minutes, each at most every 12 hours). Until then a movie has no dates and is searched
  as before.
- **Subtitles:** on. They are copied next to the video, never overwriting anything.
- **Extras:** skipped by the next library scan. That can lower the "unmatched" count you acknowledged before.

## Next

1. Try the branch on the PC: Calendar, Logs & Diagnostics, a list preview, and one upgrade on a test profile.
2. Library scans that understand anime file names.
3. Optional scheduled list sync, if wanted.
4. Trakt, if you register a Trakt app.
