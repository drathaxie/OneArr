# PROGRESS

Status as of commit d7f0ea6 (CI green). Only items marked VERIFIED were executed. Everything else is labelled.

## Goal

A Windows user installs and runs OneArr without Docker, WSL, Git, Node.js or any SDK. Updates come from precompiled
release artifacts, and the background backend runs without a dashboard window.

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
