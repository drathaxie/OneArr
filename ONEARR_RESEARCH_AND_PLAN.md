# OneArr: Feasibility Study and Build Blueprint

**Prepared:** 24 October 2026  
**Goal:** replace an approximately twelve-container *arr automation estate with a progressively consolidated, self-hosted media manager.  
**Available coding budget:** about $30 in Anthropic API credit.  
**Status:** researched design; neither source-code integration nor live-server testing has been performed. The exact existing container inventory and media layout are not yet known.

## Executive decision

A fully compatible, single-process Sonarr + Radarr + Lidarr + Prowlarr + Readarr + Bazarr replacement cannot credibly be guaranteed from one $30 Claude Code run. Shared ancestry is helpful but the applications have diverged. The highest-yield build is a **native OneArr vertical slice for movies and television**, with a reusable engine and a *temporary* integration bridge to the existing stack. Keep the existing stack in production until OneArr passes side-by-side verification.

**Do not equate:** (1) a single dashboard, (2) a single Docker container containing many upstream processes, and (3) a true unified process and database. Only the third meets the eventual architectural goal; the first is a useful transitional milestone and the second merely consolidates packaging.

### Options compared

| Approach | Engineering effort | Containers retired initially | Genuine shared internals | Main issue |
|---|---|---|---|---|
| Unified API/UI wrapper for running *arr apps | Low/moderate | None | No | Easy delivery but not a replacement |
| Multi-process all-in-one Docker image | Moderate | Several Docker entries | No | Still many independent apps, databases, memory footprints, and upgrades |
| Copy/merge entire upstream solutions in one attempt | Extremely high | Unknown | Intended | Dependency, migration, DI, namespace and test conflicts; high chance of half-working product |
| **Purpose-built .NET modular monolith, importing selective upstream logic** | Moderate to high, iterative | Can retire movie/TV managers only after proven | **Yes** | Initial functionality is intentionally limited |

## Verified source landscape

| Upstream | Source | Relevant verified detail | Proposed integration |
|---|---|---|---|
| Sonarr | https://github.com/Sonarr/Sonarr | GPL-3.0, C# with React frontend; develop `global.json` SDK `10.0.402` | Read and reference episode/release handling; read-only API migration adapter |
| Radarr | https://github.com/Radarr/Radarr | GPL-3.0; develop SDK `10.0.402` | Read and reference movie release/quality behavior; read-only API migration adapter |
| Prowlarr | https://github.com/Prowlarr/Prowlarr | GPL-3.0, .NET/React; develop SDK `8.0.421`; indexers, API and Cardigann parser | V1 accepts generic Torznab/Newznab endpoints; later port definition/engine in a contained subsystem |
| Prowlarr definitions | https://github.com/Prowlarr/Indexers | Definitions maintained separately, versioned Cardigann YML, with 500+ tracker definitions | Reuse only alongside a compatible parser and schema version; never treat YAML as an executable engine |
| Lidarr | https://github.com/Lidarr/Lidarr | GPL-3.0; develop SDK `8.0.421`; current upstream README notes metadata server recovery | Phase 2 music domain; do not assume metadata service stability |
| Readarr | https://github.com/Readarr/Readarr | Archived 27 June 2025 due to unusable metadata service | Do not build around dead metadata infrastructure |
| Librarr | https://github.com/Rorqualx/Librarr | New Readarr continuation using Open Library; README says v1.2.5-beta dated 14 Oct 2026 | Evaluate as source/reference for books, after review; not production-proven |
| Bazarr | https://github.com/morpheus65535/bazarr | GPL-3.0, Python; expressly depends on Sonarr/Radarr catalog | Phase 3: provider/plugin abstraction or independent subtitle service; no simple direct C# drop-in |
| Seerr | https://seerr.dev/ | Jellyseerr/Overseerr merged into Seerr in Feb 2026 | Requests can be a later native module; do not need to clone Seerr for v1 |
| qBittorrent | https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API | Cookie-based authenticated Web API under `/api/v2/` | Keep as separate download engine; first-class API adapter |
| TMDb | https://developer.themoviedb.org/ | Movie/TV search and metadata API requires credentials and use conditions | Initial common metadata provider, with cache, attribution, and defensive retries |
| Open Library | https://openlibrary.org/developers/api | Low-rate public APIs; explicitly not for bulk/high-volume backend use | Later human-driven book lookup, caching and rate limits; not bulk syncing |

**Branch warning:** SDK values were read from `develop` GitHub `global.json`, not tested by cloning/building particular pinned commits. Claude must pin SHA/release and verify compatibility before consuming code. Shared `NzbDrone` names do NOT imply current type/API/DB compatibility.

**GPL:** GPLv3 permits private modification without publication. If conveying combined derivative binaries to other people, comply with GPL distribution/source/notice obligations. Track source files, commit hashes, licenses and changes. See https://www.gnu.org/licenses/gpl-faq.en.html. This is engineering guidance, not legal advice.

## Architectural destination

A single ASP.NET Core 10 application serving a compiled React + TypeScript/Vite UI, with SQLite in `/config`, one bounded background task scheduler, one web port, and one media-data mount `/data`.

```text
OneArr.Web (React/Vite)
     |
OneArr.Api (ASP.NET Core, auth, versioned endpoints)
     |
OneArr.Application (commands, orchestration, jobs)
     |
OneArr.Domain
  ├── Catalog: Movie, Series, Season, Episode, Album, Book
  ├── Monitoring: WantedItem, SearchAttempt, ReleaseDecision
  ├── Downloads: QueueItem, ImportAttempt, JobState
  ├── Quality: ReleaseInfo, QualityProfile, CustomRule
  └── Settings: CredentialsRef, IndexerConfig, RootFolder
     |
OneArr.Infrastructure
  ├── Persistence: EF Core or straightforward SQLite repository layer
  ├── Metadata: TMDb initially, future MusicBrainz/OpenLibrary
  ├── Search: Torznab/Newznab; later Cardigann provider
  ├── Downloads: qBittorrent initially, future SABnzbd
  ├── Files: scanner, safe hardlink/copy/rename/import
  ├── Migration: read-only Sonarr/Radarr API clients
  └── Notifications: Jellyfin refresh/webhook later
```

Interfaces worth defining now: `IMetadataProvider`, `IIndexerClient`, `IDownloadClient`, `IReleaseParser`, `IReleaseRanker`, `ILibraryScanner`, `IImporter`, `ILegacyArrReader`, `IClock`. This is enough separation for later media domains without erecting an excessive plugin framework.

**Data model:** explicit `MediaType` enum, upstream provider ID + media type as unique external identity, normalized `Movie/Series/Season/Episode`, `QualityProfile`, monitored flags, quality cutoff, `RootFolder`, `Indexer`, `DownloadClient`, `SearchHistory`, `DownloadJob` (with correlation hash/category), `FileRecord`, `ImportRecord`, `JobLock`, `Settings`. Use migrations; never write into the original apps' databases.

**Network:** bind OneArr to a private/LAN interface unless protected by a secure reverse proxy and authentication. Never expose client API keys or TMDb bearer tokens in frontend bundles or logs. Do not grant OneArr the Docker socket or mount production configs. External URLs must be validated against SSRF risks and private-IP patterns. Keep credentials encrypted at rest where feasible and file permissions restricted; use environment variables or mounted secrets in staging.

## One-run product scope (aim, not a promise)

**Required useful native slice:** Movies and TV; search TMDb; add and monitor media; list missing movies/episodes; configure one quality profile (resolution 720/1080/2160 and preferred source; explicit quality cutoff); manual release search through one generic Torznab indexer; show scores/rejections; submit selected magnet/torrent to qBittorrent; persist jobs; inspect torrent status and import *test fixtures* safely; responsive dashboard; read-only migration preview of Sonarr/Radarr library items; Docker build and tests.

**Automated search:** only implement after manual search is proven. A bounded job may periodically query wanted releases while applying retries, indexer limits, dedupe, quality cutoffs and pause/resume; avoid hammering indexers. The first release should default automatic grabs OFF and dry-run imports ON until integration tests and filesystem test cases pass.

**Explicitly not required from first $30 run:** all 500+ native indexer adapters; full anime absolute numbering/scene mapping, special numbering, season packs; complete replacement of sophisticated release parsing, custom formats and rejection rules; music/books/comics; subtitle provider scraping; Usenet; requests/approval workflows; multi-user permissions; fine-grained notification systems; instant production cutover. Implement neither fake pages nor 'coming soon' status disguised as complete.

## Migration and data safety

1. Inventory current apps/versions and volume mappings with `docker ps`, `docker inspect`, and compose config. Never pass secrets, `docker inspect` full raw JSON, API keys, or entire config to Claude.
2. Create each native *arr backup through its built-in backup UI. Keep backups external to OneArr. Never connect OneArr to the production databases for writes.
3. Stage OneArr with a separate `/config`, and mount a **small synthetic sample `/data`** before touching real media.
4. Add Sonarr/Radarr in read-only API mode to show migration preview and source IDs; no writes, no queue grabbing, no filesystem mutation.
5. Import just small, deliberately selected metadata entries to OneArr's own SQLite database; preserve existing file path references. Existing indexer credentials and download job ownership are not migrated automatically.
6. Test release matching, tracker queries, qBittorrent jobs and importer under test-specific categories (e.g. `onearr-test`). Prove that OneArr never imports another app's downloads.
7. For every prospective retirement, compare library counts, missing flags, wanted/unwanted behavior, import paths, upgrades and quality decisions over a real observation period. Disable monitoring in the old app only **after** OneArr succeeds; keep old backups for rollback.
8. Never delete original files as a side effect of scanning/migration. Add an explicit opt-in to destructive library operations only in a later release.

**Hardlink requirements:** source and destination must be on the same filesystem, with sufficient permissions. Torrent imports should hardlink only when the filesystem supports it; otherwise copy using temporary file + integrity checks and leave the seed source alone. Validate canonical paths remain underneath configured roots. Avoid hardlinking files that a tag editor/transcoder will modify. See https://trash-guides.info/File-and-Folder-Structure/Hardlinks-and-Instant-Moves/.

Suggested target mount (illustrative, not a claim about current host):

```text
/data
  /torrents/{movies,tv}
  /media/{movies,tv}
/config
  onearr.db
  logs/
```

## Acceptance tests (must be automated)

| Test | Pass criteria |
|---|---|
| Build | `dotnet build` and `npm run build` succeed in clean environment |
| Container | `docker compose up --build` brings up only OneArr (existing qBittorrent remains an external service); health endpoint responds |
| First run | Database migrates, default admin/auth is secured, web UI loads without demo data and no secrets exposed |
| Catalog | TMDb responses (mocked for CI) create deduplicated movies and series; season/episode IDs remain stable |
| Release logic | Unit fixtures cover good resolution, bad resolution, mismatch, duplicates, upgrade cutoff, suspicious naming and multi-episode cases |
| Indexers | A fake Torznab service returns parseable releases; timeouts and bad XML do not crash jobs |
| Downloads | Fake qBittorrent API verifies cookie login and category assignment; job status persists across app restart |
| Import | Fake completed torrent imports by safe hardlink/copy into test root, no traversal, no overwrite, no source deletion; re-running is idempotent |
| Migration | Import previews read legacy API fixtures and do not call write methods on those endpoints |
| Stability | Health/readiness endpoints, bounded retries and logging; background jobs do not overlap |
| Integrity | `git diff --check`, automated tests, smoke docs, handoff with known failures |

If a live external service or Docker is missing in the Claude workspace, require *mocked integration tests* and transparently mark live integration **UNVERIFIED**. Don't fabricate tests.

## Staged replacement roadmap

| Stage | Completion evidence | Can retire? |
|---|---|---|
| 0. Inventory + native backups | Versions, data layout, rollback proven | None |
| 1. OneArr movie/TV vertical slice | Reproducible build, mocked e2e, real staging smoke test | None until parity established |
| 2. Movie parity | Search/monitor/import/upgrades/failed-grab handling on real sample library | Potentially Radarr |
| 3. TV parity | Episode/season packs, anime/specials, quality rules, retries, real runs | Potentially Sonarr |
| 4. Indexer engine | Cardigann compatibility, authenticated providers, search parity, health stats | Potentially Prowlarr |
| 5. Music/books | Metadata quality, matched import and profiles, per-medium test corpus | Potentially Lidarr/book tool |
| 6. Subtitles/requests | Independent metadata tracking + working provider integrations | Potentially Bazarr/Seerr |

Retiring containers is earned only through functional tests, not a project milestone claim.

## API budget management

As of 24 October 2026 the official first-party API list price is Claude Sonnet 5.5 **$2 per million input tokens and $10 per million output tokens**. Opus 5.5 is **$4/$20**; Haiku 5.5 is **$0.10/$0.50 for prompts up to 100K input tokens**. Cached reads can be considerably cheaper, but actual totals depend on caching, context, tools, model, and output. Source: https://platform.claude.com/docs/en/about-claude/pricing.

Illustrative uncached Sonnet 5.5 expenditure:

| Input | Output | Approx. charge |
|---:|---:|---:|
| 2,000,000 | 400,000 | $8 |
| 5,000,000 | 800,000 | $18 |
| 8,000,000 | 1,200,000 | $28 |

These are arithmetic examples, **not a forecast** for coding-agent usage. Plan up to **$23** for one implementation pass and reserve about **$7** for integration fixes. The CLI's `--max-budget-usd` is an *estimated*, print-mode cap and **can overshoot**; check actual spend in the Anthropic Console. Model alias `sonnet` may change over time, so verify `/model` or pin the exact available Sonnet 5.5 ID. See https://code.claude.com/docs/en/cli-reference and https://code.claude.com/docs/en/costs.

Cost controls: no parallel subagents, no huge repo dumps, clone upstream selectively, write a concise `PROGRESS.md`, prioritize runnable software over documentation or design polish, use a low/medium effort setting if supported, test small fixtures, avoid repeated context re-reading, and never run an expensive full-codebase rewrite to resolve a local build issue.

## Exact unanswered environment variables

These are intentionally left unknown, not invented: all actual app names/images/versions, how many user-facing domains really exist, Docker host CPU architecture and memory, filesystem locations and ownership, qBittorrent version and network reachability, chosen indexer protocol endpoints, extent of anime support, Usenet use, and whether the user wants the media managers only or also reverse proxy/VPN/Jellyfin. A `docker ps` inventory resolves most of this. **Do not block the first isolated development iteration** on unknown production values; use config values, fixtures, and adapter interfaces.

## Bibliography

- Sonarr, Radarr, Prowlarr, Lidarr GitHub repositories listed above; particularly their `develop/global.json` files.
- Prowlarr's separate indexer definitions: https://github.com/Prowlarr/Indexers
- Readarr retirement: https://github.com/Readarr/Readarr
- Librarr continuation: https://github.com/Rorqualx/Librarr
- Bazarr architecture/features: https://github.com/morpheus65535/bazarr
- Seerr merger: https://docs.seerr.dev/blog/seerr-release/
- Open Library API usage limits: https://openlibrary.org/developers/api
- qBittorrent WebUI v2 auth/API: https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-4.1%29
- Sonarr and Radarr backup procedures: https://wiki.servarr.com/sonarr/faq and https://wiki.servarr.com/radarr/faq
- TRaSH Guides hardlink explanation: https://trash-guides.info/File-and-Folder-Structure/Hardlinks-and-Instant-Moves/
- GPL FAQ: https://www.gnu.org/licenses/gpl-faq.en.html
- Anthropic pricing and Claude Code budget flags: https://platform.claude.com/docs/en/about-claude/pricing and https://code.claude.com/docs/en/cli-reference
