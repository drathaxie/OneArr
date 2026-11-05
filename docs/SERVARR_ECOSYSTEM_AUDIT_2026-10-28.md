# OneArr vs Sonarr / Radarr / Prowlarr — ecosystem capability audit

**Date:** 2026-10-28  
**OneArr code baseline:** main at 8541726264c2fd03a4e7f1c295e407202751f700 (merged PR #17).  
**Scope:** settings, workflows, integration points, operational safety and feature parity for a *Windows-native, unified movies + TV + indexer + qBittorrent manager*. This is a code/configuration audit, not a live acceptance test against every tracker or downloader.

**Upstream references:** [Sonarr settings](https://wiki.servarr.com/sonarr/settings), [Radarr settings](https://wiki.servarr.com/radarr/settings), [Prowlarr settings](https://wiki.servarr.com/prowlarr/settings), [Sonarr quick start](https://wiki.servarr.com/sonarr/quick-start-guide), [Radarr quick start](https://wiki.servarr.com/radarr/quick-start-guide). The official Sonarr settings page explicitly warns that some sections need v4 updates. Where a setting is version-dependent, verify against a running upstream release before copying defaults. Do not interpret this audit as a promise to support all third-party extensions.

## Executive conclusion

OneArr has a substantial **front half** of the workflow: TMDb discovery; movie/series and episode monitoring; library scanning/reconciliation; release searching and selection; qBittorrent categories and queue polling; native TorrentLeech and Nyaa adapters, public RSS/Atom and Torznab/Newznab; safe import previews and hardlink/verified-copy plumbing; a Windows tray/updater and acceptance tests.

Its biggest parity gap is the **back half**: completion → reliable media match → naming → safe import → file metadata → upgraded replacement → library refresh → seeding cleanup. The code already has a conservative importer but purposely runs *only import dry-run previews* from the background scheduler. There is no enabled automatic completed-download import or configurable media naming. That is an architectural omission, not a missing checkbox.

**Priority:** finish end-to-end library reliability before cloning dozens of low-impact settings controls. Avoid presenting a setting whose behavior is unimplemented.

**Status legend:** **Exists** = functional path found in current code; **Partial** = subset exists but lacks key upstream behavior; **Missing** = no implemented equivalent found; **Deliberate/optional** = upstream feature has no necessary analogue in a unified OneArr; **Unverified** = tests or code exist but live compatibility has not been established.

## P0 blockers and findings requiring explicit decisions

1. **Automatic completed download handling: missing.** BackgroundScheduler polls qBittorrent every minute; on every fifth pass it explicitly calls ImportCompletedAsync(true). The actual physical write requires a stored preview + operator confirmation. Changing DryRunImport does *not* enable scheduled physical imports. See src/OneArr.Infrastructure/Services/BackgroundScheduler.cs, DownloadService.cs, ApiEndpoints.cs.
2. **Renaming/naming tokens: missing.** SafeImporter.PlanDestination appends Path.GetFileName(source), and RelativeFolder only generates a movie or series/season folder. Imported video retains the release filename. Sonarr/Radarr-style renaming and filename templates are not present. See SafeImporter.cs, DownloadService.cs.
3. **Remote path mapping for downloader: missing.** LibraryPaths.MapLegacy and /library/mappings address *legacy Sonarr/Radarr media library path migration*. DownloadService imports the qBittorrent-reported ContentPath directly against TorrentRoot. That is **not** Sonarr/Radarr's per-client host-specific Remote Path Mapping. This will be an issue for a remote downloader or Linux paths on Windows.
4. **Manual TorrentLeech search failure reported.** The user demonstrated that a successful connection test can coexist with a TorrentLeech search returning HTTP 404. An authenticated login test proves login, not that the native search route/parameters work against the live site's current behavior. Reproduce with safe URL/path diagnostics, site fixtures and a real account before declaring tracker parity; do not log secrets.
5. **Quality upgrades are coarse.** QualityProfile contains only MinResolution, CutoffResolution and AllowWebSources. No quality/source tier ordering, custom format scoring, language preferences, release-group preferences, minimum/maximum size or explicit upgrade policy beyond basic resolution checks.
6. **Import extras, conflict/upgrade replacement and automatic cleanup are missing.** SafeImporter refuses overwrite (appropriate) and never removes sources (appropriate for seeding), but there is no staged quality-upgrade replacement/recycle workflow, subtitle/NFO import, retention management or remove-on-completed-seeding policy.
7. **Credential-at-rest policy is inconsistent.** Native TorrentLeech credentials use AES-GCM via TrackerSecretBox; generic Torznab API keys and qBittorrent passwords reside as string fields in the SQLite-backed configuration entities. The API hides secrets, which is not the same as encrypting them at rest.

## A. Media management / completed download handling

| Upstream setting or workflow | OneArr | Gap / required behavior | Priority |
|---|---|---|---|
| Monitor qBittorrent queue by category and completed state | Exists | Polls by configured client/category; preserves owner and missing/unreachable distinction | — |
| Automatically import after completion | **Missing** | Opt-in background apply, safely gated; independent of automatic grabbing | P0 |
| Manual import and review | Exists | Stored preview, fingerprint stale-check, confirmation and bulk approval | — |
| Hardlinks rather than copy | Exists | Hardlinks on same filesystem; verified copy fallback without deleting seeding data | — |
| Atomic move of non-seeding downloads | Missing | Safe move policy only when client state and ownership prove it can be moved | P2 |
| Import quiet period / safe writes | Exists | 30-second quiet period, temp file, SHA-256 copy verification | — |
| Rename movie files | Missing | Token-based naming, illegal-character/colon replacement, preview examples | P0 |
| Rename TV/anime/daily episodes | Missing | Standard, daily, absolute anime, specials, multi-episode/season pack naming | P0 |
| Movie/series/season folder formats | Partial | Fixed Movie (Year)/Series/Season NN; no token-driven customization | P1 |
| Metadata-informed naming | Missing | Resolution, source, edition, group, codecs, HDR, audio/language tokens | P1 |
| Verify matches for completed files | Partial | Episode filename parsing and unmatched records exist; richer movie ID, anime, multi-episode and multi-file matching needed | P0 |
| Import subtitles/NFO/extra files | Missing | Optional matching sidecar files, correct forced/SDH tags, source preservation | P1 |
| Video analysis (MediaInfo/ffprobe) | Missing | Probe actual files, not only release/file names, before replacing/upgrading | P1 |
| Free-space threshold and storage checks | Missing | Drive/UNC free space, verified-copy headroom, clear failure without destructive operation | P0 |
| Root folders and per-type defaults | Exists | Per-title roots, movie/series defaults, library root overview and bulk changes | — |
| Host-specific remote path mapping | Missing | Map qBittorrent's remote content paths to Windows-local paths by download client and host; prevent traversal | P0 for remote clients |
| Legacy Sonarr/Radarr path mapping | Exists | Separate migration-only path mapping; don't reuse silently as downloader mapping | — |
| Rescan library / reconcile file availability | Partial | Explicit library scan + preview/apply, missing/unreachable differentiation; no ongoing file watcher or scheduled trusted rescan | P1 |
| Unmonitor deleted files setting | Missing | Configurable behavior and safe missing-state policy | P2 |
| Create/delete empty folders | Missing | Optional no-surprise folder lifecycle | P2 |
| Recycle bin for upgrades/deletes | Missing | Recoverable replacement, restore, retention and cleanup | P1 |
| Upgrade existing file atomically | Missing | Hold old media until new file verified and database references updated | P0/P1 |
| Permissions/chmod/chown | Deliberate/optional | Windows ACL and share access diagnostics first; Unix permissions only if Linux supported | P2 |

## B. Quality profiles, custom formats, parsing, upgrades

| Sonarr/Radarr capability | OneArr | Gap | Priority |
|---|---|---|---|
| Per-movie/series quality profiles | Exists | Profiles can be assigned; defaults per media type | — |
| Create/edit/delete quality profiles | Partial | Currently GET + POST (create), no complete edit/delete workflow | P1 |
| Ordered qualities and grouped qualities | Missing | WEB-DL, WEBRip, BluRay, Remux, UHD, etc., not resolution-only | P1 |
| Upgrades allowed, upgrade until quality/score | Partial | Resolution cutoff; not profile-driven quality/CF upgrade thresholds | P1 |
| Custom formats with positive/negative scores | Missing | Import/export JSON profiles, conditions and scoring | P1 |
| Release profile required/ignored terms | Missing | Must/must-not contain, regex, release group, indexer/flags/tags | P1 |
| Audio/video language selection | Missing | Multi-language and preferred-language profiles | P1 |
| HDR, DV, codec, bit depth, audio channels | Missing | Parse actual characteristics, score and label correctly | P1 |
| Quality size boundaries | Missing | Preferred/min/max MB/min or MB/hour per quality; reject implausible size | P1 |
| Proper/REPACK handling | Partial | Basic filename ranker, no configurable proper/repack upgrade behavior | P1 |
| Delay profiles, torrent-vs-Usenet preferences | Missing | Adjustable wait windows; with torrent-only mode can start simpler | P2 |
| Anime absolute numbering, daily episodes, specials | Partial | Season/episode parser and TMDb metadata exist; full Sonarr episode taxonomy and anime-specific release matching absent | P1 |
| Rejected releases explained | Exists | Human-readable ranker reasons for title, season/episode, quality and duplication | — |
| Duplicate/regrab safeguards | Partial | Info-hash / active job detection; no complete blocklist, edition-aware or upgraded-file decision engine | P1 |

**Critical compatibility test:** Do not permit a quality-upgrade rule to delete or overwrite the only playable file until the new media has been validated, its library associations updated, and a rollback/recycle path established.

## C. Indexers and Prowlarr's indexing ecosystem

| Prowlarr / *Arr option | OneArr | Gap | Priority |
|---|---|---|---|
| Multi-indexer list, enable flags, search order | Exists | Configurable priority and movie/TV filters | — |
| Built-in catalog of known definitions | Partial | 635 cached upstream definitions; most are *metadata only*, not working adapters | P1 |
| Torznab/Newznab | Exists | Caps/read and search; missing advanced per-source category/search options | —/P1 |
| Nyaa searchable RSS | Exists | Native Nyaa RSS query and optional category/trust filters | — |
| Generic RSS/Atom | Exists | Recent-feed parsing and cache; not retrospective site search | — |
| TorrentLeech native account auth | Partial/unverified | Login/test + JSON search + torrent fetch code; reported live HTTP 404 search needs investigation | P0 |
| Dynamic Cardigann/YAML interpretation | Missing | Site-specific login, credentials, cookies, search parsers and download handlers with sandboxed definitions | P1 |
| Prowlarr native C# indexers | Missing | Listed in catalog; NOT executable in OneArr, needs adapters or interoperable engine | P1 |
| Per-indexer RSS/automatic/interactive switches | Missing | Three independent gates; current global movie/TV + enabled flags insufficient | P1 |
| Per-indexer categories and anime categories | Missing | Explicit category mapping, exclusions, supported modes, capabilities | P1 |
| Per-indexer seed/peer, ratio/time/flags limits | Missing | Minimum seeders, seed ratio/time, freeleech/required flags | P1 |
| Indexer rate limits, query budgets & backoff | Partial | Global retry/backoff exists; not configurable per indexer per-day limits/timeout | P1 |
| Indexer proxies | Missing | HTTP/SOCKS, tag-scoped proxy, secret protection; FlareSolverr integration is optional and security-sensitive | P2 |
| Prowlarr sync profiles and per-app syncing | **Deliberate** | No need to sync OneArr's own indexers into separate Sonarr/Radarr instances; optional third-party *Arr interoperability is separate | Optional |
| Test/caps/health of indexers | Partial | Connection-test health and per-search failures; live search health, statistics, search-history and proactive alerts incomplete | P1 |
| Search stats, history, indexer performance | Partial | Per-search outcome and job history; no aggregate per-tracker success/latency/rate-limit dashboard | P2 |
| Tags grouping indexers/proxies/media | Missing | Needed to scope rules and multiple libraries | P1 |
| Site-specific category/credential forms | Partial | Native TorrentLeech form, Nyaa and RSS; catalog other sites informational only | P1 |

**Important:** Catalog completeness is a metadata question. Connectivity/completeness must be measured in **working, verified adapters**, not number of names shown.

## D. Download clients, completed/failed handling, seeding

| Sonarr/Radarr/Prowlarr option | OneArr | Gap | Priority |
|---|---|---|---|
| Multiple qBittorrent clients | Exists | URL, credentials, enable flag, movie/TV categories, test | — |
| qBittorrent modern Web API and cookies | Exists | Fixed HTTP 204/no-body and QBT_SID_*; user verified login/add | — |
| SABnzbd/NZBGet | Missing | Usenet workflow including unpack, PAR repair, failure/history | P2 (if wanted) |
| Transmission, Deluge, rTorrent, others | Missing | Client abstraction allows future adapters; not implemented | P2 |
| Host/port/TLS/URL base | Partial | Single BaseUrl rather than detailed controls; reverse-proxy supported if correct URL | P2 |
| Client priority, recent/older priority | Missing | Selection is manual/limited defaults; no round robin or age-aware policy | P1/P2 |
| Per-client initial torrent state | Missing | Start paused/forced and safe seeding policy | P2 |
| Seeding ratio/time/seed goals | Missing | Configure client/indexer limits without harming tracker obligations | P1 |
| Post-import category | Missing | Optional change category once verified, caution removal compatibility | P2 |
| Remove imported torrent when seeding complete | Missing | Must NOT delete before seeding goals reached and import verified | P1 |
| Remove failed/redownload/avoid release | Missing | Per-failure blocklist and replacement search workflow | P1 |
| Torrent stalled/metadata timeout warnings | Partial | Queue status and missing/client health; finer-grained stalled/failed analysis | P1 |
| Remote download path mapping | Missing | Client-host-specific mappings, not legacy library path map | P0 for remote clients |
| Client test vs actual grab test | Partial | Tests login/category; add/download smoke not equivalent to real fully imported torrent | P1 |

## E. Monitoring, metadata, library, user workflows

| Upstream capability | OneArr | Gap | Priority |
|---|---|---|---|
| TMDb movie & TV search, posters, details | Exists | TMDb provider; no alternate provider workflow | — |
| Series/season/episode monitoring | Exists | Episode-level exceptions and migrated Sonarr monitoring; avoid resetting flags | — |
| Library scan with review, unknown/missing handling | Exists | Conservative scan plan and explicit apply | — |
| Calendar of future episodes/releases | Missing | Week/month view, missing airing/release data refresh | P2 |
| Metadata refresh and series episode updates | Missing/limited | Periodic TMDb refresh so newly aired/added episodes become searchable | P1 |
| List import (Trakt, TMDb lists, IMDb lists etc.) | Missing | List definitions, cadence, monitored/root/profile defaults, exclusions | P2 |
| Lists exclusion management | Missing | Explicit ignored titles/IDs and duplicate protection | P2 |
| Interactive, manual and wanted search | Exists | Per-indexer mode toggles, custom search overrides and better parsing remain | —/P1 |
| Automatic wanted search | Partial | Opt-in, safety gate, fixed 15-minute schedule, 10 per pass and backoff | P1 |
| Tags and bulk editor | Partial | Bulk root folder change exists; broad tags, profiles, monitoring mass editing absent | P1 |
| History, queue, import records | Exists | Job history and per-search outcome; missing full release history, rejection metrics and audit export | —/P2 |
| Missing/unmatched-file remediation | Partial | Visibility and review; richer manual match/renaming workflow needed | P1 |
| Alternate editions / multi-version movies | Missing | Proper movie edition handling and multiple file associations | P2 |

## F. Automation, notifications, integrations and system settings

| Upstream option | OneArr | Gap | Priority |
|---|---|---|---|
| Background scheduler | Exists | 1m queue sync; 5m **dry-run only** import planning; 15m wanted search with global grab gate | — |
| Configurable task intervals, immediate refresh | Partial | Manual endpoints exist; no user-configurable cadence/task scheduler | P2 |
| Automatic completed-download import toggle | Missing | Distinct from automatic grabbing; opt-in only after safety review and migration validation | P0 |
| Event/webhook notifications | Missing | Grab/import/upgrade/rename/failure/health -> webhook, Discord, email, Jellyfin/Plex refresh | P1/P2 |
| Jellyfin/Plex server library update | Missing | Optional server scan notify after successful import (never required for import itself) | P1/P2 |
| Health warnings/recovery alerts | Partial | Connection tests, queue health and errors; no centralized notification engine | P1 |
| Server host, bind address, port | Partial | ONEARR_LISTEN_ADDRESS and ONEARR_PORT env vars; no UI equivalent | P2 |
| Security (login, password rotation, API tokens) | Partial | Password setup + PBKDF2 and bearer tokens, but no password-change UI, role/API key lifecycle | P1 |
| Secret encryption at rest | Partial | Native tracker creds encrypted, other client passwords and keys stored server-side as strings | P1 |
| HTTP/SOCKS general proxy, cert policy | Missing | No unified configurable outbound network policy | P2 |
| Logs: severity, retention, export | Partial | Job history and runtime/tray logs, no comparable logging settings UI | P2 |
| Backup/restore and scheduled backup retention | Partial | Windows backup script, updater preservation/rollback; no first-class timed DB+key backup & tested restore wizard | P1 |
| Application updates/release channel | Partial | Windows tray self-updater/rollback workflow; release channels and UI controls limited | P2 |
| UI calendar/date/timezone/localization/theme | Partial | Posters/list preference and system light/dark, no full UI/calendar/locale settings | P2 |
| Metadata NFO/artwork consumers | Missing | Optional Kodi/NFO/metadata export; do not confuse with TMDb search | P2 |
| Prowlarr apps sync | Deliberate/optional | Unnecessary within unified OneArr; external Prowlarr/Sonarr/Radarr compatibility API optional | Optional |

## Proposed OneArr Settings information architecture

Keep the existing sidebar but **only add controls when the backend enforces them**:

1. **Media Management** — Naming (Movies / Series / Daily / Anime); Importing (automatic vs manual, hardlink, free space, sidecars); File Management (upgrades/recycle/cleanup); Roots and client-path mappings; Library review.
2. **Profiles** — Quality profiles; Custom Formats; Quality Definitions / Size; Delay / Release terms.
3. **Indexers** — Catalog; Connected/health; Search modes; Categories/limits; Advanced auth and proxies.
4. **Download Clients** — qBittorrent connections; Categories; Seeding/handling; Remote path mapping; Future clients behind adapters.
5. **Lists & Metadata** — TMDb metadata refresh; Lists, exclusions; optional server metadata.
6. **Automation** — Wanted searches; Completed Download Handling; Scheduled Tasks; failed search/retry policies.
7. **Connect / Notifications** — Jellyfin/Plex and webhooks, event triggers.
8. **General / System** — Security, backup/restore, network, updates, logging and health.
9. **UI** — Existing poster/list and theme choices; future date/localization options.
10. **Migration** — Keep visible as an exceptional one-time workflow, **not** a runtime path mapping or nightly synchronization.

## Sequenced implementation plan with measurable acceptance criteria

### Phase 0 — Close integrity hazards and live tracker 404
- Reproduce TorrentLeech live search HTTP 404 with a real test account or sanitized request/response path capture; add a redacted fixture and correct adapter route/auth flow without sending cookies off-origin.
- Add per-client host-specific remote path mapping and Windows tests for case-folding, UNC paths, traversal/reparse-point rejection and unreachable shares; leave legacy migration mappings independent.
- Add free-space and permission diagnostics before any physical write. No overwrite, no source deletion.
- Improve content matching confidence for downloads with multiple media files, season packs, anime and ambiguous titles; route low-confidence cases to review.
- **Gate:** no automatic import enabled until recovery tests and source/target path safety pass.

### Phase 1 — Completed Download Handling and naming
- Add explicit settings: **Automatic completed download handling (default OFF)**, import mode (hardlink preferred/copy allowed), naming enabled, movie/series/season/daily/anime templates, preview examples.
- Build deterministic filename planner with whitelist tokens, sanitization, conflict checks and preview. Maintain filename/release-group provenance.
- Implement background import of only **fully completed + reliably matched + safe** jobs. Use same fingerprint/revalidation as manual preview/apply; operator review required on any ambiguity.
- Do not link or delete seeding payloads incorrectly; confirm completion and safe destination. Validate multi-file and interrupted-copy idempotence; audit every imported path.
- **Gate:** Windows acceptance suite proving finished torrent → renamed hardlinked/copy-verified file → correct movie/episode availability, with two-volume and same-volume scenarios, seeding preserved, no manual click for high-confidence jobs.

### Phase 2 — Quality engine and upgrade lifecycle
- Ordered quality/source profiles, explicit upgrading rules, minimum/maximum quality-size definitions, HDR/DV/audio/language preferences, custom-format scoring, source/group preferences, anime/daily/repack parsing.
- Genuine upgrades: compare current media with candidate, stage new file, verify before replacing references, recycle old file, restore on failure; never remove torrent before seed requirements.
- **Gate:** same library tested across HD/UHD, multi-episode, anime, multi-version, duplicate and rollback scenarios.

### Phase 3 — Indexer and downloader controls
- Full native Cardigann-compatible definition executor (and applicable native site adapters), category/search-mode mappings, per-indexer limits/flags, measured live search health and failure backoff.
- qBittorrent seeding policy, priority and completed/failed handling; other clients (Transmission/Usenet) if product scope warrants.
- **Gate:** every "supported" indexer completes auth/test/search/download acceptance; unsupported catalog rows remain informational.

### Phase 4 — Operations and integration
- Metadata refresh, calendar, tags, bulk editor, import lists/exclusions, notification triggers, Jellyfin/Plex refresh.
- Automated backups including **SQLite database plus secret.key**, restore rehearsal, password rotation, sensitive settings encryption and explicit log/retention preferences.
- **Gate:** no silent loss of credentials, files, monitoring or import history across upgrade/reinstall/restore.

### Phase 5 — Optional parity, not mandatory for native replacement
- External *Arr Prowlarr sync/API compatibility, additional platform/Usenet consumers, localization and advanced automation.
- Preserve OneArr's one-process Windows-native goal; do not reintroduce Docker or a Prowlarr service as a requirement.

## Verification checklist / user acceptance

1. qBittorrent test succeeds AND an actual search and torrent-add work (already confirmed by user for qBittorrent; do not conflate with TorrentLeech search success).
2. OneArr observes a 100% complete torrent; import is safely deferred while bytes change and never changes qBittorrent's seeding file.
3. Same NTFS volume: hardlink (same content; no duplicated disk use) under correctly named movie/season folder; different volume: verified copy; source preserved until any explicit seeding cleanup policy.
4. Series pack: files match distinct season/episode keys, extras handled properly, unknown episodes require review.
5. Existing file of different content is never overwritten; upgrade policy stages/verifies and provides recovery.
6. UNC shares, inaccessible drives, hostile symlinks/junctions and remote paths fail safely with clear status, not silent "missing" or wrong media state.
7. A search path that returns HTTP 404 despite login success is surfaced accurately with safe diagnostics; adapter fixed against live behavior.
8. SQLite/secret.key backup and restore round-trip preserves sources, media, credentials and monitoring.
9. Publish a native Windows snapshot with complete installation/upgrade acceptance before declaring the feature shipped.

## Audit boundaries and provenance

- **Direct OneArr code examined:** ApiEndpoints.cs, Program.cs, Domain/Entities.cs and Settings.cs, BackgroundScheduler.cs, WantedSearchService.cs, DownloadService.cs, SafeImporter.cs, LibraryReconciler.cs, LibraryPaths.cs, MediaService.cs, ReleaseService.cs, ReleaseRanker.cs, TorznabClient.cs, PublicRssClient.cs, NativeTrackerClient.cs, QBittorrentClient.cs, settings.tsx and API contracts. README.md/PROGRESS.md are historical context only; actual code prevails.
- **Upstream taxonomy:** Servarr's published Sonarr, Radarr and Prowlarr settings references linked above. Upstream versions change; specific provider- and OS-dependent flags may need follow-up against current release schemas.
- This audit **does not** claim all upstream plugins, third-party indexer definition implementations, tracker credentials, or a physical Windows media library were live-tested. It inventories controls and workflows and names those verification gaps.
- **Scope discipline:** a unified OneArr has no need to emulate Prowlarr's internal *Arr-to-*Arr indexer sync for its own operations. That is optional interoperability, not a blocker for OneArr's target user.
