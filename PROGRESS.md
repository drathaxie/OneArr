# PROGRESS

Status as of commit d7f0ea6 (CI green). Only items marked VERIFIED were executed. Everything else is labelled.

## Goal

A Windows user installs and runs OneArr without Docker, WSL, Git, Node.js or any SDK. Updates come from precompiled
release artifacts, and the background backend runs without a dashboard window.

## Windows acceptance checklist for Jesse's PC (NOT run in the sandbox — no Windows, no real library there)

Run these against a release-shaped build of the PR #5 head, with the real library, before merging.
Safety rules for every step: do NOT press Apply on any monitoring sync, do NOT move/rename/delete
media files, keep imports dry-run until verified, and keep automatic grabbing OFF (Settings →
Automation must read off before and after).

1. **Install/startup.** Launch the published build (API + tray) from a fresh folder. Record: time from
   launch to `/health` answering, and whether the tray icon appears and the dashboard opens.
2. **Sign-in.** First-run setup, then log out and back in; confirm the password still works after a
   full stop/restart (the CI acceptance run checks this on a throwaway config; this step checks it on
   the real config).
3. **Settings persistence.** Set a movie default root, a series default root, and a per-type default
   profile in Settings → Media Management / Profiles. Stop, restart, and confirm all four values
   read back unchanged. Do not toggle grabbing on for this check; only confirm the toggle reads OFF
   and that a failed settings save (e.g. a relative path) leaves it OFF.
4. **Series page at real scale (~205 series).** Scroll the poster grid end to end; record any
   stutter, blank posters, or wrong badges. Spot-check: at least one Complete, one Partial, one
   Missing, and one Unknown series, and confirm each classification against its episodes (monitored
   + aired + file present, by hand, for one title each).
5. **WWE/AEW monitoring exceptions.** Open the Details page for a WWE/AEW series with intentionally
   unmonitored old episodes. Confirm: the unmonitored episodes do NOT count as missing, the Wanted
   count excludes them, and no season toggle has flipped them to monitored. Do NOT run a sync Apply
   to "fix" anything seen here.
6. **Wanted counts.** Compare the Wanted view's counts against a manual count for one series
   (monitored aired episodes without files). Confirm unmonitored and unaired episodes are excluded.
7. **Add dialogs.** Open Add Series and Add Movie: confirm each prefills its OWN default root and
   profile (series dialog must not show the movie root), and that with a default cleared the dialog
   starts empty with guidance and a disabled Add button. Add nothing for real in this step, or add a
   throwaway test title and remove it from OneArr afterwards (removal from OneArr only — no files).
8. **Settings layout.** View Settings at a normal width and at ~800px narrow: sidebar must stack
   above content, every category must open, Advanced toggle must hide/show path mappings and
   connection tests only.
9. **Files and paths.** Open Details → Files for a title with linked files; confirm the listed files
   match disk and that nothing was moved, renamed, or rescanned destructively. Confirm the media
   root in Settings still contains every title's root folder, or note any title that would now fail
   closed on import.
10. **Update/rollback.** Record the current installed version and config backup location before
    replacing the build; after running the new build, confirm the old build + config restore still
    starts (rollback path). Record release folder size and idle RAM/CPU from Task Manager (the CI
    acceptance run reports idle private memory and CPU on a throwaway config; this step is the real
    one).
11. **Screenshots (genuine, from this run only).** Capture: (1) Settings overview + sidebar,
    (2) Media Management + default roots, (3) Series page with real posters, (4) Add Series dialog,
    (5) one Complete/Partial/Missing/Unknown card each, (6) Series Details with season/episode
    monitoring toggles. Attach them to PR #5. No mockups, no cropped synthetic libraries.

What CI already covers (so the PC run does not need to redo it): Linux + Windows backend tests
(including the Windows filesystem regression suite), frontend vitest + build, a synthetic
20,000-movie Windows library benchmark (timings, not Jesse's library), and the headless Windows
acceptance run (startup, health, sign-in, settings persistence across restart, single-instance
refusal, graceful shutdown, idle CPU/memory gates) — all on throwaway configs, never a real library.

## PR #5 review fixes (branch `settings-ui-overhaul`, same PR, still open and unmerged)

Both points from the PR #5 review were addressed; neither changes the UI architecture, and neither
merges, publishes, or enables automatic grabbing.

1. **No more shared-root fallback for adds.** `MediaDefaults.ResolveRoot` no longer takes a legacy
   root at all: a per-type default, or empty. `POST /api/media` with no explicit root and no per-type
   default now returns 400 naming the type and where to set the default, instead of silently landing a
   series in `D:\Media\Movies` (or a movie in the TV folder) whenever that path was the shared root.
   The Add dialog mirrors this: it starts empty when its type has no saved default, shows guidance
   pointing at Settings → Media Management, keeps Add disabled until a folder is typed, and no longer
   reads the legacy localStorage root at all. The media root itself stays as a real setting, relabelled
   "Media root (imports must stay inside it)": it is the import containment root (imports fail closed
   outside it), and Settings now says plainly that it is not an Add default. A title's root should sit
   inside the media root, or its later imports will fail closed.
2. **Settings saves are atomic.** `PUT /api/settings` now validates the entire request first (absolute
   paths, profile existence) and only then commits everything with one new
   `SettingsStore.SetManyAsync` call (one context, one SaveChanges). A failed field can no longer leave
   earlier fields changed while returning an error — including the automatic-grabbing toggle, which is
   the field where a half-applied save would be dangerous. The endpoint's request/response shape is
   unchanged, and existing settings tests still pass against it.
- New tests for these fixes (backend for CI, frontend executed here): `AvailabilityTests` ResolveRoot
  cases now assert empty with no per-type default; `SettingsDefaultsApiTests.FailedPutWritesNothingIncludingGrabbing`
  asserts a failed PUT (bad root, then bad profile id) leaves grabbing, torrent root, and series root
  untouched; `add-dialog-defaults.test.tsx` asserts a series dialog with no series default but a
  movies-looking shared root starts empty, shows the guidance, keeps Add disabled, and posts only the
  typed folder. Frontend suite is now 24 passed, build 224 KB JS.

## Settings & Library UI overhaul (branch `settings-ui-overhaul`, from main d81cdf9 — PR #4 merged)

Note: PR #4 was merged into main (d81cdf9, tag v0.4.0) before this work started, so this branch is
based on main and its PR targets main, not `visual-library`. Nothing here merges, publishes, or enables
automatic grabbing; grabbing stays off by default and is only changeable from Settings → Automation.

### Settings navigation (new `web/src/settings.tsx`)
- Settings overview with a card per category, a persistent sidebar with expandable categories and
  subsection links, clear active-section highlighting, and a Show Advanced / Hide Advanced toggle
  (stored in localStorage `onearr.advanced`, default on). Advanced items are path mappings and
  connection tests; hiding them never removes the underlying functionality. Below 860px the sidebar
  stacks above the content. Theme handling is unchanged (system theme; dark when the system is dark).
- Categories, all backed by existing functionality only: Media Management (per-type default roots,
  import paths, bulk root folders, library folders/scan/review, path mappings), Profiles (quality
  profiles + per-type default profiles), Indexers, Download Clients, Metadata (TMDb), Automation
  (toggle + safety gate), Migration (the existing Migration component, unchanged), General
  (application note + security status; there is deliberately no password-change screen, which OneArr
  does not support), UI (Posters/List preference + advanced toggle).
- The old flat Settings page was split: TMDb into Metadata, torrent/media roots and dry-run into
  Media Management, the grabbing toggle into Automation. Profile/indexer/client creation forms are
  unchanged in behaviour; connection tests are now scoped per category (TMDb only in Metadata, etc.).

### Per-type defaults (backend, no database migration)
- New `AppSetting` keys only (no schema change): `media.defaultRoot.movie`,
  `media.defaultRoot.series`, `profile.default.movie`, `profile.default.series`. GET/PUT `/api/settings`
  exposes them; PUT validates roots as absolute (empty clears, null leaves unchanged) and rejects
  unknown profile ids. Pure helper `MediaDefaults.ResolveRoot` resolves movie defaults only from the
  movie default and series defaults only from the series default — so a movie root can never become
  a series default. (Historical note: the first version of this section said the resolver fell back
  to the legacy single media root when a per-type default was unset. That fallback was removed by the
  PR #5 review fix at the top of this file: with no per-type default, adds now require an explicit
  folder instead of silently inheriting the shared root.) POST `/api/media` uses it when no
  explicit root folder is sent; re-adding an existing title still returns it unchanged (root, profile,
  and monitoring are never overwritten by a later add).

### Movies/Series pages and the Add dialog
- The permanent TMDb add/search panel and the bulk root-folder panel are gone from both pages.
  An Add Movie / Add Series button opens a dialog: TMDb search, result selection, root folder and
  quality profile prefilled from the per-type server defaults, monitoring scope (movie: Monitor
  checkbox; series: All seasons / First season only / None), and a confirmation summary before the
  Add button sends anything. (Historical note: the first version of this section said the legacy
  localStorage root was a last-resort fallback on first run. That fallback was removed by the PR #5
  review fix at the top of this file: the dialog no longer reads the localStorage root at all, and
  with no saved per-type default it starts empty with guidance instead of prefilling any folder.)
- Poster grid, List view, search, sort, filters, and per-title monitoring controls are retained.
  Series filters now include Complete/Partial/Missing/Unknown. Per-title root, profile, and monitoring
  overrides remain on each title's Details page. Bulk root-folder changes live in Settings → Media
  Management, per media type.

### Accurate series availability (backend, one aggregate query)
- `GET /api/media?type=series` and `GET /api/media/{id}/detail` now report episode-based availability:
  Complete / Partial / Missing / Unknown plus `availableEpisodes` / `applicableEpisodes` counts
  (e.g. "126 / 180 monitored aired episodes available"). Applicable means monitored with a known air
  date on or before today; unaired episodes and unmonitored episodes (including Sonarr per-episode
  exceptions inside a monitored season) never count as missing. Applicable == 0 (no episodes, no
  monitored episodes, or no monitored episode with a known air date) is Unknown, never Complete or
  Missing. The series-level `HasFile` flag is no longer used for the series badge: a series with one
  downloaded episode cannot read as Complete. Movies keep Available/Missing from `HasFile`.
  `MediaService.SeriesAvailabilityAsync` is one grouped query for the whole library, not one request
  per poster card. Scan-review and manual-search behaviour are untouched, and monitoring sync still
  touches only Monitored flags (see the PR #4 sections below).

### Poster grid polish (CSS only, no new dependencies)
- Two-line titles via line clamping with a fixed two-line height so cards stay aligned; full title
  on hover via the `title` attribute; consistent 2/3 poster aspect ratio including the fallback;
  cards are equal-height flex columns; badges are legible and distinct (Complete green, Partial
  amber, Missing red, Unknown neutral); lazy loading and the poster fallback are unchanged.
  Bundle: 223 KB JS (66 KB gzip), up from 205 KB (62 KB) — the new Settings/dialog code, no new framework.

### Tests actually executed
- Frontend, in this Linux sandbox: `npm test` (vitest) — 23 passed (was 14; new: settings navigation
  and per-type defaults, Add Series dialog incl. first-season scope, series availability badges, plus
  updated settings tests for the split forms), and `npm run build` (tsc + vite) — passed.
- Backend: NOT executed in this sandbox — no .NET SDK is installed here, and no backend test run is
  claimed. New backend tests added for CI: `AvailabilityTests` (Complete/Partial/Missing, no-episode
  Unknown, unaired + unmonitored exclusion, unknown-air-date Unknown, ResolveRoot cross-type
  separation, classification boundaries, re-add never overwrites) and `SettingsDefaultsApiTests`
  over the real API host (defaults start empty, per-type independence, empty-string clearing,
  relative-root rejection, profile existence validation). CI on the new PR is the backend verification.

### Remaining verification (not done here)
- CI backend tests for this branch, and a Windows run with a real or realistic large library
  (including ~205 series) to check poster-grid performance, the new Settings layout at narrow
  widths, and the Add dialog against a real TMDb key.
- Screenshots for the PR (Settings overview, sidebar, simplified Series page, Add Series dialog,
  Complete/Partial/Missing cards) were not captured in this sandbox: the frontend build runs, but
  there is no live backend here to sign into and render a populated library. They should be taken
  from the Windows run and attached to the PR before it is reviewed as final.
- General → Security honestly notes there is no password-change screen yet; that is a missing
  feature, not a setting that was hidden.

## Review fixes round 2 to PR #4 (branch `visual-library`, same PR, not merged)

1. **Preview comparison is now identity-strict.** `PreviewMatches()` previously compared only the counts of
   changed seasons and episodes, so a preview for episodes 1 and 2 would pass after the source moved to
   episodes 3 and 4. `MonitoringChange` now carries `SeasonChanges` and `EpisodeChanges` — each with its exact
   identifier (season number, or season + episode number) and its old and proposed monitored values — populated
   by sync for both the per-episode and the season-fallback paths. `PreviewMatches()` compares those lists
   by identity (order-insensitive), in addition to the previous title/series-flag/count checks. The sync
   response now includes the detail lists, the UI shows them (e.g. `S1E2 on→off`), and the frontend sends
   them back verbatim with the Apply request. Backend test: same counts with different episode identities,
   different season identities, and different proposed values all reject; the identical preview still passes.
2. **Missing Sonarr series ids now mark episodes unknown.** The reader previously skipped episode fetching
   when the Sonarr series `id` was absent but left `EpisodesUnknown=false`, so sync fell back to season flags
   and could overwrite per-episode exceptions. Now any Sonarr series for which an episode-level read cannot
   be performed — missing id, not only a failed request — is marked `EpisodesUnknown`, and sync leaves its
   episodes alone (the same handling as a failed episode read). Backend test: a series record with no `id`
   produces `Episodes=null, EpisodesUnknown=true`, and no episode request is made for it.

**Tests actually executed for round 2**
- Frontend, in this Linux sandbox: `npm test` (vitest) — 14 passed, and `npm run build` (tsc + vite) — passed.
- Backend: NOT executed in this sandbox — no .NET SDK is installed here, and no backend test run is claimed.
  New backend tests added for CI: identity-strict preview rejection (episodes, seasons, values), sync detail
  population for a per-episode exception, and the missing-Sonarr-id unknown case. CI on PR #4 is the backend
  verification for these fixes as well.

## Review fixes to PR #4 (branch `visual-library`, same PR, not merged)

Four review findings, all fixed on the same branch:

1. **Preview missed series/movie-only monitored changes (bug, fixed).** `SyncLegacyMonitoringAsync` compared
   `item.Monitored` to its original value after conditionally updating it, so during Preview (no update) a
   series-level-only change was never reported. Now `monitoredChanged` is computed before any mutation, is
   reported as its own field in every change, and preview and apply report the same change set.
2. **Episode-level Sonarr exceptions (now supported, not just documented).** The reader now also reads
   `GET /api/v3/episode?seriesId={id}` for each Sonarr series (one GET per series, a one-off migration cost).
   Sync applies per-episode flags, which win over season flags, and episodes Sonarr does not report are left
   alone. New imports pass episode flags through to `AddAsync`, which also prefers them over the season flag.
   If an episode read fails, that series is marked `EpisodesUnknown`: its season flags still sync, but its
   episodes are left alone and the series is listed in the sync result and in the UI as unknown, instead of
   being silently forced to its season flag.
3. **Apply now requires a valid preview.** The API returns 400 if `apply: true` arrives without a preview,
   recomputes from the current source and database, and returns 409 with the fresh preview if the source,
   the database, or any proposed change moved since the preview the user saw. The Migration UI keeps the
   preview bound to its source: Apply is disabled until a preview exists for the current kind + URL, changing
   kind, URL, or key clears the preview, and after a 409 the user must Preview again. Loading the regular
   migration preview no longer clears the API key (it previously forced retyping the key before a sync);
   the key still lives only in session state and is never stored.
4. **Missing season data no longer defaults new imports to all monitored.** `/migration/import` now safe-skips
   a new series with no season monitoring data, reporting `skipped` with the reason, instead of setting
   `MonitorAllSeasons=true`. An already-imported series still reports `exists` (its monitored state is not
   touched by a re-import). If episode data was unavailable for an otherwise successful import, the result
   carries a warning that episodes took their season flags and monitoring sync should be run later.

**Tests actually executed**
- Frontend, in this Linux sandbox: `npm test` (vitest) — 14 passed (was 13; 1 new Migration gate test:
  key survives loading preview, Apply is disabled until a current-source preview exists, and changing the
  source clears it), and `npm run build` (tsc + vite) — passed, 205 KB JS (62 KB gzip).
- Backend: NOT executed in this sandbox — no .NET SDK is installed here, and no backend test run is claimed.
  New backend tests added for CI: preview reports a series-only monitored change without writing; per-episode
  exceptions inside a monitored season are preserved and an unreported episode is left alone; unknown episode
  data leaves episodes alone while seasons still sync; the reader parses episodes and marks unknown on episode
  read failure; and the preview-match and series-import-skip rules (including the drifted-preview case).
  CI on PR #4 is the backend verification for these fixes as well.

**Remaining verification for these fixes**
- CI backend tests for this commit, plus a real Sonarr run: Preview sync must show per-episode exceptions
  (e.g. a deliberately unmonitored episode inside a monitored season) as unchanged, and a series whose episode
  read fails must appear in the unknown list with its episodes untouched.
- A real import of a series Sonarr reports without seasons must show `skipped`, not `added`, and must not
  create an all-monitored series.
- Apply with a stale preview (change Sonarr monitoring between Preview and Apply) must be refused with the
  fresh preview, and Apply must stay disabled until a new Preview for the current source is run.

## Visual library + monitoring fidelity (branch `visual-library`, from main 27fb5df, PR #3 merged)

**Source of truth.** Started from main 27fb5df ("Merge pull request #3", v0.3.0 tag present on main). Nothing from
the v0.3.0 branch was rebuilt: reconciliation, path mapping, backup scripts, connection tests, missing-download
blocking, and Windows packaging are all reused as merged.

**What changed, by priority**
- P1 poster grid (Movies, Series): poster cards are now the default view, with a Posters/List toggle that is remembered
  in localStorage. Cards show the TMDb poster (lazy-loaded, `w342`, with a title fallback when there is no poster),
  title, year, Monitored/Unmonitored, Available/Missing, and quality profile. Search, sort (title, year, available
  first), and filters (available, missing, monitored, unmonitored) are client-side over the existing `/api/media`
  list, so no new list endpoint was needed. Partial is deliberately NOT shown in the grid: the list response only
  carries a single have/missing flag per item, so a series card says Available or Missing, and the real
  Partial/Missing/Available breakdown lives on the detail page where episode data is known.
- P2 detail pages: clicking a card or title opens a detail view (no router change; it is page state, so Back returns
  to the grid). It shows poster, title, year, overview, monitored state, quality profile, root folder, file status,
  and linked files, plus release/manual search actions (movies, and per-episode for series), root-folder editing,
  and quality-profile changing. Series detail adds a season-by-season view with collapsible seasons, per-season
  monitored toggles, per-season have/wanted counts, and episode rows (number, title, air date, monitored,
  have/missing, file resolution from the linked file row, not the filename). The old inline episode list in the
  Movies/Series table was replaced by this detail view; its per-episode release action is preserved there.
- P3 monitoring fidelity (the root cause of inflated wanted counts): the Sonarr reader previously dropped the
  `seasons` array entirely, and migration import forced `MonitorAllSeasons=true`, so every season and episode of
  an imported series became monitored even when Sonarr had seasons unmonitored. Now: `LegacyItem` carries seasons,
  the reader parses them, new imports keep Sonarr's season flags, and a new `POST /api/migration/monitoring-sync`
  (preview by default, explicit Apply with a confirmation in the Migration UI) syncs monitored flags into
  already-imported items matched by TMDb id, without creating items or duplicates. It touches only Monitored flags
  on the item, its seasons, and episodes in synced seasons; it never touches titles, quality profiles, root folders,
  or file state, and it never forces episode flags when a legacy series reports no seasons array. New endpoints
  `PATCH /api/media/{id}/seasons/{season}` (season + its episodes) and `PATCH /api/media/episodes/{episodeId}`
  (single episode, season flag left alone) back the detail-page toggles. Wanted is computed from monitored flags
  as before, so after a sync the wanted counts fall to the real monitored set.
- P4 Wanted: filters by movies/episodes, search, and download state (in progress vs none); episodes are grouped by
  series with collapse/expand; every row links to its detail view; and the page now states plainly that Wanted lists
  only monitored items with no file, with the unmonitored without-file counts shown alongside so legacy/unmonitored
  data is visible instead of hidden. "Why wanted" is always "no file (monitored, aired)" for this list, because that
  is what Wanted actually computes. Unmatched, ambiguous, sample-only, misnumbered, and inaccessible files are NOT
  wanted items — they live in the Library scan review, and the Wanted page says so with a pointer to Settings.
  A new `hasActiveDownload` flag (Queued/Downloading/Completed jobs only; Missing/Failed do not count) distinguishes
  "wanted, download running" from "wanted, nothing happening".
- P5 Library/files: scan review rows now show the catalog title next to the file path (links, confirmed missing, and
  unknown rows via mediaItemId; ambiguous rows show candidate titles), so review no longer means matching bare paths
  to titles from memory. No file information was removed, and apply still never moves, renames, copies, or deletes a file.
- P6 Settings: a new Automation status card at the top of Settings shows the grabbing toggle state together with the
  library automation gate and its blocking reason, in plain language, instead of leaving the toggle and the gate in
  separate sections. Each library root now has a live Test folder button (new read-only
  `POST /api/library/roots/{id}/test`; it does not scan, does not update the stored last-scan state, and does not
  touch files in the folder).
- P7 stack discipline: still React + the existing ASP.NET Core API, no Electron, no Chromium embedding, no new UI
  framework, no image library. Posters are plain `<img loading="lazy">` against TMDb's image CDN, and the grid is plain
  CSS. Bundle went from 188 KB JS (57 KB gzip) to 204 KB JS (61 KB gzip) in the sandbox build — a modest increase
  for the grid + detail views, not a new framework.

**Tests actually executed**
- Frontend, in this Linux sandbox: `npm ci`, `npm test` (vitest) — 13 passed (was 10; 3 new poster-grid/detail tests:
  grid badges and poster fallback, search + list toggle, card → detail with linked files), and `npm run build`
  (tsc + vite) — passed.
- Backend: NOT executed in this sandbox — no .NET SDK is installed here, and no backend test run is claimed.
  New backend tests were added and must pass in CI: Sonarr seasons parsing, monitoring-sync preview (no writes)
  then apply (season + episodes flip, series flag and file state untouched, Wanted drops to zero with the
  unmonitored count reported), monitoring-sync without a seasons array (episodes left alone), and season/episode
  toggle semantics. CI on this PR is the backend verification for this branch.

**Real versus simulated**
- Real: frontend tests exercise real request/response handling at the fetch layer (with a fake fetch). No real
  Sonarr, Radarr, TMDb, qBittorrent, or library was contacted from this sandbox, and no live test is claimed.
  The monitoring sync was verified only against the test fixtures above; the first real run must be Preview on the
  Windows PC against the real Sonarr/Radarr, reviewed, and only then Applied.

**Remaining blockers**
- CI backend tests and the Windows acceptance run for this PR (required; backend was never compiled here).
- Real Sonarr monitoring sync (Preview → review → Apply) on the Windows PC, especially the WWE/AEW series that
  motivated P3, and confirmation that wanted counts fall to the real monitored set without losing wanted items
  that should stay wanted.
- Real poster rendering at library scale (hundreds of cards) on the Windows PC; the sandbox only checked the
  component logic with two items, not rendering performance with a real library.
- A release candidate and its publication, both requiring explicit authorization (not done here).
- Automatic grabbing stays off by default throughout; enabling it on the PC is a separate, explicit decision
  after the library is reconciled and monitoring is synced.

**Exact instructions for testing on the Windows PC**
1. Back up `%LOCALAPPDATA%\OneArr\config` first. Do not stop or change Radarr/Sonarr containers.
2. Build this branch on Windows from a clean checkout (per README, developers only) and run it with a disposable
   config folder, or, once authorized, use the manual `release` workflow and install that zip instead. Do not
   install the test build over `%LOCALAPPDATA%\OneArr` until steps 3–6 pass.
3. Open Movies and Series: confirm the poster grid is the default, search/sort/filters work, cards show the right
   monitored and available/missing badges, and the List toggle still shows the old table.
4. Open a series detail page: confirm seasons collapse/expand, per-season and per-episode monitored toggles work,
   wanted counts look right, and linked files show real paths, sizes, and resolutions.
5. On Migration, with the real Sonarr URL and key: run Preview monitoring sync first, review every change
   (especially WWE/AEW seasons), and only then Apply. Then check Wanted: it should no longer be flooded with
   episodes from unmonitored seasons, and the unmonitored counts should explain the remainder.
6. In Settings → Library, use Test folder on each root, then run a preview scan and confirm titles now appear
   next to file paths in the review lists. Keep automatic grabbing off until monitoring is synced and the
   library shows as reconciled.

## v0.3.0 readiness (branch `v030-work`, from main 4144235)

**What this branch does, and what it deliberately does not do.** It does not reimplement reconciliation,
path mapping, automatic-grabbing safeguards, native Windows hardlinks, backup scripts, or validation tooling:
all of those are already merged on main. It adds only the missing controls and diagnostics needed to use them
on a real Windows PC, and it does not merge, publish a release, enable automatic grabbing by default, delete,
rename, move, or modify any media file, or touch Radarr, Sonarr, or any other container.

**Implemented**
- Automatic grabbing toggle in Settings → General (Phase 5). It uses the existing `PUT /api/settings`
  `automaticGrabbing` field, which previously had no UI control, and replaces the stale "Automatic grabbing is
  not implemented in this build" text. Default remains off. Turning it on does not bypass the reconciliation
  gate: searches still only run when the Library panel shows the library as reconciled, every folder is
  reachable, and no download is active for an item.
- Linked files for a catalog item (Phase 2). New read-only `GET /api/media/{id}/files` returns the files
  OneArr already recorded in its own database (path, size, resolution, status, match method) plus the item's
  root folder and have/missing state. It never scans the disk. Movies and Series pages gain a Files button
  showing that list. The Library preview "Will link" table now also shows file size and detected resolution,
  which were already in the preview response but not rendered.
- Non-destructive connection tests (Phase 3/5). New `POST /api/connections/test` with Settings buttons for
  TMDb, each Torznab indexer, and each qBittorrent client, and a Test connection button on the Migration
  page for Radarr/Sonarr. TMDb: one test search. Torznab: the caps document (categories and search/TV/movie
  search availability) via a new pure `TorznabClient.ParseCaps`. qBittorrent: sign-in plus torrent counts
  per configured category, torrent states, and whether any item reports a content path (paths themselves are
  not returned). Radarr/Sonarr: one read-only catalog read, reporting readable/error counts and sample
  titles. Nothing is added, downloaded, imported, or written to a legacy application. Stored keys/passwords
  are used server-side only and never returned; a body-supplied legacy key is used for one test and never
  stored. Failures return a clear `ok: false` diagnostic instead of a bare HTTP error.
- Stuck-download reconciliation (Phase 4). A download marked Missing in qBittorrent no longer silently
  triggers a re-grab: automatic search skips it with a new `SkippedMissingDownload` outcome (reconsidered
  after 1 hour, not immediately), and a manual grab is refused with a message pointing at Queue. Queue gains
  a Clear missing button for Missing jobs only, which changes OneArr's record to Failed and leaves every
  torrent and file untouched. Only then can the item be searched or grabbed again.
- Phase 1 version and docs: product version in `OneArr.Api` and `OneArr.Updates` is now 0.3.0 (the release
  workflow still sets the exact version at publish time). README's Docker section now says Docker is an
  optional, unverified developer path, not the v0.3.0 Windows path. The upgrade/rollback machinery itself
  was audited and not changed: the release workflow gates on tests and an acceptance run, EF Core
  `MigrateAsync` upgrades the database at startup, the updater backs up the database before an update and
  preserves a failed update's database, and `INSTALL.txt` already documents that config and backups survive
  updates and uninstall. The tray-acceptance default of v0.2.0 was left alone on purpose: it tests a
  published tag, and v0.3.0 is not published yet.

**Tests actually executed**
- Frontend, in this Linux sandbox: `npm ci`, `npm test` (vitest) — 10 passed (was 8; the 2 new tests cover
  the automatic-grabbing toggle submitting `true` and the TMDb connection-test button showing its result),
  and `npm run build` (tsc + vite) — passed, web bundle 188 KB JS (57 KB gzip).
- Backend: NOT executed in this sandbox — no .NET SDK is installed here, and no backend test run is claimed.
  New backend regression tests were added and must pass in CI: `TorznabClient.ParseCaps` (categories/search
  flags, rejection of non-caps and invalid XML) and two missing-download tests (automatic search skips a
  Missing job with `SkippedMissingDownload` and no second grab; manual grab is refused while Missing).
  Existing backend tests are unchanged in intent; CI on this PR is the backend verification for this branch.

**Real versus simulated**
- Real: the frontend tests exercise real request/response handling at the fetch layer (with a fake fetch).
  The connection-test endpoints, when run on a Windows PC with real services, make real read-only calls.
- Simulated / not live: no real TMDb, Torznab, qBittorrent, Radarr, or Sonarr service was contacted from
  this sandbox, and no live test is claimed. The new backend tests use the existing fake indexer/qBittorrent
  fixtures. The staging end-to-end workflow (Catalog → Search → Release evaluation → qBittorrent submission
  → Download status → Safe import → Library reconciliation → Available) already exists as
  `tools/windows/staging-check.ps1` and is the procedure for real-service testing, not something re-run here.

**Resource measurements**
- No new Windows runtime measurements were taken in this sandbox (Linux, no .NET, no Windows runner here).
  The last Windows measurements on main remain the run 40205398175 acceptance numbers (16/16 checks,
  startup 1.83 s, idle private memory 36.2 MB, idle CPU 0.44%). New Windows startup/idle/scan measurements
  for this branch must come from CI's acceptance run and from the PC test below; do not carry the old
  numbers forward as this branch's results.

**Remaining blockers**
- CI backend tests and the Windows acceptance run for this PR (required before any release candidate).
- Real-service connection tests on the Windows PC (TMDb, Torznab caps, qBittorrent, Radarr/Sonarr) — the
  in-app buttons plus `tools/windows/staging-check.ps1` with disposable folders and authorized test content.
- Reconciliation of the real movie/TV libraries on that PC, including a complete scan apply and the
  Automatic grabbing panel showing "reconciled".
- A v0.3.0 release candidate and its publication, both requiring explicit authorization (not done here).
- Automatic grabbing stays off by default throughout; enabling it on the PC is a separate, explicit decision
  after the library is reconciled, and it is not part of this branch's verification.

**Exact instructions for testing the release on your Windows PC (controlled test, no production changes)**
1. Back up your existing data first: copy `%LOCALAPPDATA%\OneArr\config` somewhere safe. Do not point
   this test at your production config, and do not stop or change your Radarr/Sonarr containers.
2. Build the branch on Windows from a clean checkout (per README, developers only): `cd web; npm ci; npm run build; cd ..; dotnet build OneArr.slnx -c Release`, then run it with a disposable config folder,
   e.g. `ONEARR_CONFIG_DIR=C:\OneArrTest\config dotnet run --project src\OneArr.Api` — or, once authorized,
   use the manual `release` workflow with version 0.3.0 and install that zip instead. Do not install the
   test build over `%LOCALAPPDATA%\OneArr` until steps 3–6 pass.
3. Sign in, open Settings, and use the new Connection tests: Test TMDb, each indexer, each qBittorrent
   client. On Migration, use Test connection for Radarr and Sonarr before Load preview. Expect a clear
   ok/error message for each; no key or password should ever appear in the UI.
4. In Settings → Library, add your Windows movie and TV folders, add Docker-to-Windows path mappings from
   the suggestions, then Preview scan. Check the Will link table (now with size and resolution), Ambiguous,
   Unmatched, Unknown, and Confirmed missing lists. Apply only after a complete scan, with unmatched files
   acknowledged if you intend to allow automatic grabbing later.
5. On Movies and Series, open Files for reconciled items and confirm the actual paths, sizes, and
   resolutions match the files on disk. Confirm items that were previously shown as "missing" now show
   as having files, without any file being moved, renamed, or deleted.
6. In Queue, confirm any Missing download is not re-grabbed automatically, and that Clear missing (only
   for Missing jobs) lets that item be searched again. Keep automatic grabbing off until step 4 shows the
   library as reconciled, then decide explicitly whether to turn it on in Settings.

## Windows validation (branch `library-validation`, verified at 064ab97)

**GitHub state.** PR #1 (library reconciliation) was merged by `drathaxie` into `main` at `6ee76f8`. No release has been
published since `v0.2.0`, so no installed copy contains the reconciliation work. Nothing was merged or released in this step.

**CI at 064ab97 (run 40205398175): success.** Windows job: 214 cross-platform tests and 18 Windows filesystem tests, 0 failed.
Library benchmark step and acceptance run passed. Linux job passed.

**Defects found by validation and fixed (each with a regression test)**

- A folder that went offline during a scan could leave its files "missing", which clears `HasFile` and sends items back to search.
  Fixed: every accessible folder is re-checked after probing. Missing and unknown decisions under a folder that dropped are withheld.
- Review lists grew with the library. Fixed: lists are capped at 1,000 and the counts are exact.
- Apply tracked every entity it wrote, so change detection grew quadratically. Fixed: tracking is cleared after bulk writes and
  after each recalculation batch. Found while doing this: switching off automatic change detection silently dropped updates.
  Fixed by requesting an explicit change scan before every save, and a multi-batch test reads the database back.
- The earlier benchmark asserted only the planned count, so an apply that wrote nothing would have passed. It now reads the
  database back, and the same check runs in the Windows job.
- Validation script: `Read-Host` ignores redirected input and returns an empty answer. Prompts now refuse anything except the typed
  word. Repeated mapping parameters and REST array responses were mishandled. Both fixed and re-run.

**Scripted validation, sandbox run (Linux, real API, generated media, seeded Docker-path catalog).** Not the user's library.

| Check | Result |
|---|---|
| Automatic grabbing checked, not changed | off before and after |
| Catalog checked | 7 items (6 movies, 1 series), 0 with a file before |
| Files discovered | 6 |
| Confirmed matches (after mapping `/data/media/...`) | 4, applied after typed confirmation |
| Ambiguous (two catalog entries, same title and year) | 1, not linked |
| Unmatched (no year in name) | 1, acknowledged, not linked |
| Items Missing → Available | 4 |
| Wanted movies before → after | 6 → 3 (the three with no file remain wanted) |
| Second run | 0 links, 4 already linked, rows unchanged |
| File deleted, then re-validated | item marked Missing only after typed `MISSING`; returned to wanted |
| Root folders reassigned (6 entries) | mappings kept |
| Original media files (SHA-256, before/after) | identical in every run |

**Benchmark, synthetic library of 20,000 movies with real folders and a file-backed SQLite database.** Measured, not claimed for
any real library. Figures are one run each.

| Phase | Windows (GitHub runner, 4 logical CPUs) | Linux sandbox (4 logical CPUs) |
|---|---|---|
| Enumerate folders and files | 987 ms wall, 1,109 ms CPU, 32 MB allocated | 396 ms wall, 571 ms CPU, 37 MB |
| Match files (indexed) | 179 ms wall, 188 ms CPU, 92 MB allocated | 534 ms wall, 72 MB |
| Preview (enumerate, match, probe, store plan) | 1,796 ms wall, 1,938 ms CPU, 227 MB allocated | 1,361 ms wall, 184 MB |
| Apply (re-scan, writes, recalculation) | 4,937 ms wall, 5,766 ms CPU, 965 MB allocated | 4,013 ms wall, 934 MB |
| Second preview, no changes | 820 ms wall, 922 ms CPU, 191 MB allocated | 382 ms wall, 167 MB |
| Cancel a preview 50 ms in: stopped after | 163 ms | 301 ms |
| Peak working set of test process | 338 MB | 561 MB |

Cross-checked against the downloaded `windows-library-benchmark` artifact from run 40205398175
(`build/library-benchmark-windows.txt`): every Windows figure above matches the artifact exactly. Two
artifact lines were not in the table before and are added here for completeness, clearly labelled:
fixture generation (not reconciler work) took 24,419 ms wall, 8,266 ms CPU, 47 MB allocated, and the
benchmark test itself passed in 59 s (1 test, 0 failed).

**Acceptance run, same Windows job (run 40205398175, `windows-measurements` artifact,
`build/measurements.json`).** This is the release-shaped build acceptance run, not the synthetic-library
benchmark and not a real user library: 16 of 16 checks passed, startup 1.83 s, graceful stop 0.56 s
(exit 0), idle CPU 0.44% over 60 s, idle private memory 36.2 MB, idle working set 108.1 MB, 14 threads,
release folder 224.4 MB (361 files), database 172.0 KB, config folder 212.1 KB.

Apply's memory is the largest cost and it grows linearly: at 40,000 movies (Linux sandbox) it allocated 1.86 GB, against 934 MB at
20,000. Before this change, apply allocated 2.2 GB at 20,000 files. The remaining cost is spread across the re-scan, the inserts,
and the recalculation. Reduce it further only with a measured profile.

**What is fake and what is real.** Reconciler tests use an in-memory disk (`FakeLibraryFs`) and in-memory SQLite. The benchmark and the
scripted run use real folders and a file database, but synthetic or seeded data. The scripted run used the real API.

**Not verified.**

- The user's real Windows installation and real library. No file on that machine has been touched by this work.
- Radarr and Sonarr metadata migration, and TMDb lookups. No credentials or services were available, so no live results are claimed.
- A real network share going offline. The mid-scan case is tested with a fake disk only.
- Permission-denied folders on a real Windows ACL.
- Duplicate-download behaviour under live automation. Automatic grabbing stayed off in every run.

**Procedure for the user.** `tools/windows/library/README.md` covers backup (stop OneArr, then `backup-onearr.ps1`), mapping and
root-folder setup, the read-only preview, the typed confirmations, verification, and rollback. Upgrading to a build of this branch
needs a release, which has not been authorised.

## Library safety hardening (branch `library-reconciliation`, verified at b8117a7)

**Verified on GitHub Actions (run 40202436157, commit b8117a7):** both jobs pass. Windows: 211 cross-platform tests
(0 failed), 18 Windows filesystem tests (0 failed), release-shaped publish, acceptance run on Windows. Linux: pass.

**Earlier verified result (commit 587c3c2, run 40200870468):** Windows job passed with 186 cross-platform tests and 18
Windows filesystem tests. This was the first commit where Windows CI ran the NTFS library tests.

**Changes**

- Automatic grabbing fails closed. It starts only when a complete apply has reviewed every monitored catalog entry
  (`MediaItem.LibraryReviewedUtc`), every enabled folder was read completely by that apply, the folder set and path
  mappings match that scan, nothing is ambiguous, unmatched files are acknowledged, and every folder is reachable now.
  Migrations never set the review flag, so a migrated catalog stays blocked until an operator applies a complete scan.
  Applying a scan never enables automatic grabbing.
- Partial scans: a walk is complete only if it read every folder, stayed under the file cap and finished within the time
  limit. Files in an unreadable folder become `Unknown`, not `Missing`. `Unknown` never clears `HasFile`, and an
  incomplete scan never reviews the catalog. A probe distinguishes Exists, Missing and Unknown; `File.Exists` returns
  false on access denied, so it could not do this.
- Time limits: enumeration stops after 15 minutes and the walker stops at its next folder. A listing that is blocked
  inside the OS cannot be interrupted, so that thread finishes on its own. Cancellation is honoured per folder and per
  file.
- Previously imported catalogs: re-running the migration refreshes missing legacy paths, keeps user edits, and creates no
  duplicates (tested through the real endpoint on existing items).
- Bulk root-folder reassignment (`POST /api/media/root-folder`) and mapping suggestions
  (`GET /api/library/mapping-suggestions`) are in the API and the Movies/Series pages and Library panel.
- Matching uses a catalog index instead of a scan of the whole catalog per file.

**Performance (one run each, Linux sandbox, real files on disk, in-memory SQLite)**

| Phase                         | 20,000 movies | 40,000 movies |
|-------------------------------|---------------|---------------|
| Enumerate folders and files   | 217 ms        | 454 ms        |
| Match (indexed)               | 410 ms        | 791 ms        |
| Preview (enumerate+match+store) | 1,040 ms    | 1,759 ms      |
| Apply (database writes)       | 5,909 ms      | 12,076 ms     |
| Second preview, no changes    | 570 ms        | 1,458 ms      |
| Cancel a running preview      | 226 ms        | 440 ms        |
| Peak working set (test process)| 636 MB       | 910 MB        |

Not measured: Windows timings, network shares, real Sonarr/Radarr libraries. Apply is the slowest phase, and cancellation
latency grows with library size because some phases check the token per batch rather than per row.
Reproduce with `ONEARR_BENCH=1 ONEARR_BENCH_MOVIES=20000 dotnet test tests/OneArr.Tests --filter Benchmark`.

**What is fake and what is real**

- Fake: `FakeLibraryFs` (in-memory disk) for the reconciler tests, including unreadable folders and undecidable files.
  `TestDb` is in-memory SQLite. The walk tests use a fake listing.
- Real: `LocalLibraryFileSystem` against folders on disk, in the Linux sandbox and in the Windows CI job (junctions,
  missing folders, the probe). The migration upgrade test runs on a real SQLite file from the previous schema.
- Not real: Sonarr/Radarr, TMDb lookups, real network shares, and a real drive that goes offline.

**Not verified**

- A permission-denied folder on Windows (only simulated).
- Radarr/Sonarr re-import with a live TMDb lookup (needs credentials).
- Timings on Windows and on a real network share.
- A native folder picker: not built. Paths are typed.

## Existing-library reconciliation (branch `library-reconciliation`)

**Problem fixed:** items imported from Radarr/Sonarr showed as missing although the files existed. Nothing linked files on
disk to catalog items, the migration dropped the original paths, and existing items could not get a Windows folder.

**Built**

- Additive migration `AddLibraryReconciliation`: `LibraryRoots`, `PathMappings`, `MediaFiles`, `MediaFileEpisodes`,
  `LibraryScanPlans`, and `MediaItems.LegacyPath`/`LegacySource`. The `Up` method only adds. A test upgrades a database
  from the previous schema and checks that existing rows survive.
- Matcher (pure, no I/O). Rules, strongest first: the catalog's legacy folder, then a TMDb id in the path, then title and
  year. Movies need a year. Series use the series folder plus `S01E02`, `S01E02E03`, `S01E01-03` and `1x02`. Ambiguous and
  unmatched files are reported and never linked. Files named as samples are never linked.
- Path rules: Windows drive and UNC validation. A Linux path on Windows is refused with guidance. Keys are case-insensitive
  on Windows. Path mapping uses the longest matching prefix on whole segments. A stored Linux path is never used on Windows.
- Disk reads are read-only. Junctions and symlinks are counted and skipped, never followed. Reachability checks time out
  after 10 s. A drive that is not ready is reported as unavailable.
- Preview changes no media. It only stores the reviewed plan.
- Apply runs a fresh scan and writes only entries that still match the reviewed plan (same file, size and links). Excluded
  entries are skipped. Apply never moves, renames, copies, links or deletes a file.
- `HasFile` turns on for a verified link. It turns off only when every linked file is confirmed missing on an online
  folder and no completed download import exists for that item. An offline folder marks files `Inaccessible`, never missing.
- Automatic grabbing pauses until every enabled folder has been scanned and applied, and is reachable at the time of the
  search.
- API: `GET /api/library/status`, `POST/DELETE /api/library/roots`, `PUT /api/library/mappings`,
  `POST /api/library/preview`, `POST /api/library/plans/{id}/apply`, and `PATCH /api/media/{id}` accepts `rootFolder`.
  Migration import keeps the legacy path. An existing value is never overwritten.
- UI: Settings → Library panel (folders, path mappings, preview, apply with confirmation). Linux placeholders on Windows
  paths are replaced. The migration page sends the original path.

**Verified locally (Linux sandbox)**

- 185 non-Windows tests pass. These include the matcher, path rules, the reconciler on a fake disk, the library endpoints,
  and the upgrade test.
- Frontend: type check, build, and 8 Settings tests pass.

**Not verified yet**

- `LibraryWindowsTests` (real NTFS scan, byte-for-byte unchanged check, junctions, unavailable drive, case-insensitive
  folder) passed in the Windows CI job at 587c3c2 and b8117a7.
- A real Radarr/Sonarr migration with TMDb lookups (needs your credentials).
- An offline UNC share on a real network. The code path is tested with a fake disk.
- Scan speed on a large library (not measured).
- Folder picker: not built. Folders are typed.
- Changing a movie's folder from the Movies page: API only, no UI yet.

## VERIFIED

### CI on GitHub (`drathaxie/OneArr`, run 40162471451, commit d7f0ea6)
| Job | Result |
|---|---|
| Linux: backend tests excluding Windows-only tests | 118 passed |
| Windows: frontend build and 8 Settings tests | pass |
| Windows: backend tests excluding Windows-only tests | 118 passed |
| Windows: Windows filesystem regression tests (junctions, hardlinks, case, shared reads) | 9 passed |
| Windows: publish self-contained win-x64 build (API and tray) | pass |
| Windows: acceptance script against the published build | 16 of 16 checks passed |

Acceptance checks on Windows: backend starts and answers `/health` (startup 1.83 s); health reports the build version;
database ok; web UI served; dashboard requires sign-in (401); first-run setup issues a token; dashboard loads with the
token; settings save and read back; second instance on the same configuration refuses to start (exit 3); graceful stop
exits with code 0 (1.04 s); stop-request file consumed; restart works; password and settings persist across restart.

### Measured on the GitHub Windows runner (idle backend, no tray, no UI open)
| Measure | Result |
|---|---|
| Startup to healthy | 1.83 s |
| Background CPU, 60 s window after 30 s warm-up | 0.16 % |
| Private memory after warm-up | 37.5 MB |
| Graceful stop | 1.04 s |

The first run measured 8.36 % CPU because the sample started during startup. The warm-up is therefore part of the method.
Working set, thread count, footprint and database size are written to `measurements.json` by the acceptance script and are
attached to releases; they are not yet read back from a release.

### Bugs found by CI and fixed
- Three Windows junction tests failed in *cleanup* (recursive delete cannot remove a junction). Cleanup now removes links only.
- Acceptance CPU sampling was taken during startup. Now warmed up first.

### Local
- `dotnet build` of the solution: 0 warnings. Non-Windows tests: 118 passed.

## NOT VERIFIED

- **Release artifact: not published.** Creating a tag from this environment is refused by the sandbox git proxy (both
  annotated and lightweight tags fail with "remote end hung up"). Branch pushes work. The release workflow can be started
  manually: GitHub → Actions → `release` → Run workflow → version `0.2.0`. That workflow gates on the same tests and
  acceptance run, then publishes `OneArr-0.2.0-win-x64.zip`, its `.sha256`, and `…-measurements.json` to a GitHub release.
- **Tray application end to end: not run in CI.** The WinForms tray, its Start Menu shortcut, the Run-key autostart, the
  release-check and install flow, rollback from the tray menu, and `--uninstall` are compiled but have not been exercised on
  Windows. The update and rollback state machine is covered by 13 cross-platform tests with fake release and process doubles.
- **Real GitHub release download:** the updater's download and checksum path is tested with fake HTTP only.
- **Resource numbers for the tray process** (WinForms, about 40 MB expected but not measured) and the installed footprint of
  the self-contained release (not measured; the release run will report it).
- **Real imports against a real torrent client or indexer:** not tested. Import logic is tested against local fixtures.
- **Docker:** the Dockerfile is kept as an optional path and is no longer required. Its last verified build needed a
  sandbox CA workaround (see previous history).
- **Single-file tray publish:** built in CI, but the released exe has not been launched on a clean machine.

## Known limits

- Hardlinks only on the same NTFS volume. Other filesystems fall back to a verified copy.
- Mount points and junctions in the media or torrent root are refused; the root itself must be a real folder.
- Imports wait 30 s after a file's last write before taking it.
- Automatic grabbing is off by default; it can be turned on with `PUT /api/settings {"automaticGrabbing": true}`. It also
  waits for the library scan described above.
- Movies without a year in their folder or file name are not matched automatically.
- Junction and symlink folders in a library root are not scanned, so files behind them are not linked.
- Upgrades (replacing a file below the quality cutoff) are not searched.
- The tray does not update itself. To update the tray, run the installer from the newer release folder while the old tray
  is closed.

## Next

0. Library: check that the Windows CI job ran `LibraryWindowsTests`, then preview a real library folder before applying.
1. Run the release workflow for 0.2.0 from the Actions page, then check the assets and checksum.
2. Install the released zip on a clean Windows machine: run the tray, check the Start Menu entry, autostart, the update
   check, install, rollback, and uninstall with `--uninstall`.
3. Read the released measurements file and record the tray's own memory and the installed footprint here.
