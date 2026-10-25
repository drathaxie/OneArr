# PROGRESS

Status as of commit d7f0ea6 (CI green). Only items marked VERIFIED were executed. Everything else is labelled.

## Goal

A Windows user installs and runs OneArr without Docker, WSL, Git, Node.js or any SDK. Updates come from precompiled
release artifacts, and the background backend runs without a dashboard window.

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
- Automatic grabbing is off by default; it can be turned on with `PUT /api/settings {"automaticGrabbing": true}`.
- Upgrades (replacing a file below the quality cutoff) are not searched.
- The tray does not update itself. To update the tray, run the installer from the newer release folder while the old tray
  is closed.

## Next

1. Run the release workflow for 0.2.0 from the Actions page, then check the assets and checksum.
2. Install the released zip on a clean Windows machine: run the tray, check the Start Menu entry, autostart, the update
   check, install, rollback, and uninstall with `--uninstall`.
3. Read the released measurements file and record the tray's own memory and the installed footprint here.
