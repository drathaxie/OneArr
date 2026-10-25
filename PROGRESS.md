# PROGRESS

Status as of commit d7f0ea6 (CI green). Only items marked VERIFIED were executed. Everything else is labelled.

## Goal

A Windows user installs and runs OneArr without Docker, WSL, Git, Node.js or any SDK. Updates come from precompiled
release artifacts, and the background backend runs without a dashboard window.

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
