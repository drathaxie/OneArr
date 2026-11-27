# OneArr status and roadmap

Updated 2026-11-26, at `main` 3e92e8f plus branch `release-timing`. Latest release: v0.7.8.

"Done" means on `main` and covered by tests. "Unverified" means it works in tests but has not been confirmed on the
Windows PC with the real library. Local logs on that PC have not been reviewed from a cloud session (no folder shared).

## Where we are

### Catalog and metadata

| Item | Status |
|---|---|
| TMDb search and add, movies and series | Done |
| Fix match (rematch an entry, keeps episode ids and history) | Done |
| Specials (season 0), unmonitored by default | Done; unverified against a live show |
| Manual "Refresh metadata" | Done |
| Scheduled refresh of series episode lists | **This branch** (every 5 min, 5 series, 12 h cycle) |
| Air *times* | **This branch**: exact times from TVmaze (matched by TVDB/IMDb id, used only when within a day of TMDb's date); falls back to the date |
| Movie digital/physical release dates | Missing |

### Library

| Item | Status |
|---|---|
| Reconcile existing files (preview, typed confirm, apply) | Done; used on the real library |
| Automation pauses until the catalog is reviewed (fail closed) | Done |
| Partial scans, offline shares, mid-scan drop-outs | Done (fake-disk tests; no real share outage tested) |
| Docker path mappings, bulk root folders | Done |
| "Find on TMDb" for unmatched shows and movies | Done |
| Ignore list for extras (featurettes, webisodes) | Missing: they reappear on every scan |

### Search and grab

| Item | Status |
|---|---|
| Wanted search (scheduled, rate-limited, backoff) | Done |
| New episodes searched before the backlog; hourly retries for 2 days after airing | **This branch** |
| RSS sync (grab from indexers' recent feeds) | **This branch**: opt-in per indexer (off by default), every 15 min, single episodes and movies only |
| Indexers: Torznab, Nyaa, generic RSS | Done |
| TorrentLeech native | Partial |
| Quality profiles | Partial: resolution only; no source tiers, size limits, custom formats |
| Upgrades | Partial: resolution cutoff only, no replacement of an existing file |

### Downloads and import

| Item | Status |
|---|---|
| qBittorrent, multiple clients, per-client path mapping | Done |
| Automatic import (opt-in) | Done; v0.7.5 fixed held jobs hiding importable ones |
| Visible hold reasons and empty-download explanations | Done (v0.7.8); unverified on the real queue |
| Samples excluded from import and matching | Done |
| Problems tab with Dismiss | Done |
| Renaming on import (opt-in) | Done |
| Free-space check before copy | Done |
| Subtitles and other extras | Missing |
| Upgrade replacement and recycle bin | Missing |

### Operations

| Item | Status |
|---|---|
| Windows tray, self-install, release-based updates and rollback | Done |
| CI on Linux and Windows, release workflow | Done |
| Viewing logs from the app | Missing: diagnosing your PC needs logs pasted or a shared folder |
| Notifications (new download, import failure) | Missing |
| Calendar of upcoming episodes | Missing |

## Why South Park was not grabbed automatically

Three causes in the code; this branch fixes the first two and softens the third.

1. **Episode lists were never refreshed on their own.** If TMDb listed the new episode after the series was last fetched,
   OneArr did not know it existed. Fixed: scheduled refresh.
2. **New episodes waited behind the backlog.** The search queue was ordered by due time and each run searches 10
   items, so any overdue backlog item went first. Fixed: recently aired, then never searched, then backlog.
3. **"Aired" is a date, not a time.** An evening episode counted as aired that morning (UTC), was searched before
   any release existed, and then waited 6 hours. Fixed: exact air times from TVmaze (South Park: 22:00 New York, which is
   03:00 UTC the next day), plus hourly retries for 2 days after airing, plus RSS sync.

## Next, in proposed order

1. ~~RSS sync~~: on this branch. Turn it on per indexer in Settings → Indexers → Edit → "RSS sync".
2. **Log viewer and diagnostics export.** Show recent warnings and errors in the app, plus a "download diagnostics"
   button, so problems on the PC can be read without sharing folders.
3. ~~Air times~~: on this branch (TVmaze). Filled in by the next metadata refresh of each series (within 12 hours).
4. **Ignore list for unmatched extras**, and a simple specials monitor toggle per season.
5. **Quality profiles with source tiers and size limits**, then upgrades that replace a file safely (recycle bin).
6. **Subtitles and extras on import.**
7. **Calendar and notifications.**

Decided 2026-11-26: use the best source for each job rather than TMDb for everything (TVmaze for air times); RSS sync
runs only on indexers you mark for it.
