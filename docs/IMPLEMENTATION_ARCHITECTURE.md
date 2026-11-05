# OneArr implementation architecture — unified Servarr parity

Baseline: docs/SERVARR_ECOSYSTEM_AUDIT_2026-10-28.md, main d9d1ee0. This is a forward-looking implementation design, not a claim that all phases have shipped.

## Constraints and architecture

OneArr stays a self-contained Windows-first application; do not require Docker, Sonarr, Radarr or Prowlarr at runtime. Keep SQLite migrations backward compatible. Preserve monitoring decisions, download ownership, existing files, seeding sources, and the persistent secret.key. Options belong in the UI **only when the backend enforces them**.

- Catalog domain: TMDb identities, movies, series, seasons, episodes, monitored state, real file metadata.
- Indexer domain: verified adapters, RSS vs searchable feeds, capabilities, categories, modes, auth, rate limits and safe errors. Catalog entries are not automatically usable adapters.
- Download domain: qBittorrent and future clients, torrent states, seeding, categories, client-owned paths and credentials.
- Import domain: remote-path translation, exact media matching, naming templates, fingerprinted preview, validated apply, immutable seeding source and error recovery.
- Quality domain: ordered sources/tiers, scores, languages, codecs, HDR, custom formats, upgrade limits and trusted release provenance.
- Operations: scheduler, health, notifications, optional Jellyfin/Plex refresh, backups and Windows upgrade/restore.

## End-to-end state machine

Discovery → wanted item → indexer query → release evaluation → qBittorrent grab → owned queue item → verified completion and file stability → remote-path resolution → exact media match → planned filename → fingerprinted import → verified hardlink/copy → library availability update → optional media server refresh.

Ambiguous identity, unknown client path, unrecognized extension, changing bytes, full drive, existing different target or insufficient source data MUST stop at review. Automatic grabbing and automatic importing are separate opt-ins, initially OFF. Never delete a seeding source or overwrite existing data. New-quality upgrades must be staged, verified, recoverable and idempotent.

## Storage plan

Global settings with few values use typed validations through SettingsStore; entity relationships (profiles, tags, client paths, policies, notification targets) require versioned EF migrations. Backups must include SQLite plus secret.key. Secrets should be authenticated-encrypted, omitted from responses and redacted in diagnostics. Disabling automatic-import dry run must not enable scheduled writes.

## Easiest safe tasks first — implementation sequence

| Slice | Deliverable | Dependencies |
|---|---|---|
| F1 | Complete quality profile edit/delete UI/API; enforce valid values and referential safety | none |
| F2 | Naming templates and deterministic preview, no file writes | none |
| F3 | Per-indexer RSS/automatic/interactive modes, categories and search gates | migration |
| F4 | Tracker health distinguishes login versus real search; reproduce TorrentLeech HTTP 404 with redacted diagnostics | live fixture |
| F5 | Client-specific remote-path mapping and safe Windows/UNC containment checks | download ownership |
| F6 | Import free-space checks, robust filename/media matching, multi-episode and anime cases | F5 |
| F7 | Explicit opt-in completed-download import with existing fingerprinted preview/apply and safe retry | F2,F5,F6 |
| F8 | Ordered quality tiers, custom formats, size/codec/language/HDR rules and upgrading | F1,F6 |
| F9 | Safe version upgrades/recycle bin, sidecars, metadata-refresh worker and cleanup | F7,F8 |
| F10 | More native Cardigann definitions, per-tracker budgets and optional additional clients | F3,F4 |
| F11 | Tags, import lists, calendar, notifications, Jellyfin/Plex and configurable tasks | reliable import events |
| F12 | Security policies, encrypted secrets, backups/restore validation, logging/network/update controls | all migrations |

Every slice must have matching API, UI, service, tests, any required migration, and Windows acceptance. A catalog entry alone is not a working tracker.

## F1 quality-profile management

Existing QualityProfile has Name, MinResolution, CutoffResolution and AllowWebSources and only GET/POST endpoints. Add PUT /api/quality-profiles/{id}, DELETE, validation and authenticated frontend. Editing preserves stable IDs and updates decisions for assigned media without moving files. Deletion MUST reject if referenced by MediaItem, selected as default movie/series profile, or the only remaining profile. Use 409 Conflict for in-use profiles. UI shows Edit and requires confirmation before Delete. No migration, import automation or file writes.

## F2 naming planner contract (preview first)

Whitelist tokens: Title, Year, Season, Episode, EpisodeTitle, Quality, Source, ReleaseGroup, OriginalName and extension. Filename templates cannot contain path separators or traversals; sanitize Windows reserved names/characters, trailing spaces/dots and excessive lengths, and retain original extension. Provide representative previews and deterministic tests; never guess ambiguous metadata. Only after F7's import safety work is complete should the planner feed stored fingerprints and physical imports.

## Rollout and acceptance gates

For each PR: Linux dotnet and frontend tests, Windows filesystem and snapshot acceptance when relevant. Verify exact main commit after merge. Keep manual importer and dry-run behavior; don't turn on automatic imports or downloads as a side effect of quality/profile/UI improvements. Physical import/replacement requires real local Windows smoke test before claiming production parity.
