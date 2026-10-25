# PROGRESS

Everything marked VERIFIED was executed. Everything else is labelled. Integration with real services is **not** claimed
anywhere in this file: all upstream services were simulated with fakes that run inside the test process or on localhost.

## Verified (commands executed, results observed)

| Item | Command | Result |
|---|---|---|
| Solution build, Release | `dotnet build OneArr.slnx -c Release --no-incremental` | 0 errors, 0 warnings |
| Backend tests | `dotnet test OneArr.slnx -c Release` | 88 passed, 0 failed |
| Frontend typecheck, build, Settings tests | `npm run build`, `npm test` (in `web/`) | pass (8 Settings tests) |
| Frontend audit | `npm audit` | 0 vulnerabilities |
| End-to-end smoke against fake upstreams | `bash tools/smoke/smoke.sh` | 27 of 27 checks passed |
| Container image build | `docker build` (see caveat below) | image built, all three stages |
| Container start and health | `docker run ... onearr:local` | `/health` ok, UI served, API requires token |
| Migrations on a fresh container database | container logs | `InitialCreate` and `AddSearchAttempts` applied |
| Data survives container restart | restart, then login and read settings | login and settings persisted |

### Docker caveat (read this)

- The Docker daemon was started inside the sandbox (`dockerd`, vfs storage, iptables off). Base images pull.
- The sandbox's network proxy re-signs TLS traffic with its own CA. Containers do not trust that CA, so `npm ci`
  and `dotnet restore` fail inside a plain `docker build`. The image that was verified was built from a temporary
  Dockerfile that installed the sandbox CA into the build stages. The repository Dockerfile was not changed for this.
- On a normal host with a normal network, the repository Dockerfile is the one to use.

### Bugs found and fixed in this run

- **Dockerfile could not build at all.** It copied `OneArr.sln` (the file is `OneArr.slnx`) and `tests/`, which
  `.dockerignore` excludes. Both lines were removed. The earlier "Docker not verified" status was correct, but the
  Dockerfile itself was also broken.
- **Importer followed symlinked directories.** Only a symlink as the final file was refused. A symlinked folder in the
  torrent or media tree could lead outside the root. Every component below the root is now checked, including dangling links.

## Automation (added in this run)

- **Wanted detection:** monitored movies without a file, and monitored aired episodes without a file. Items with an
  active download (queued, downloading, completed but not imported) are skipped.
- **Scheduled Torznab search:** runs every 15 minutes in the scheduler, only when `automation.grab` is on. It is off by default.
- **Grab selection:** the best accepted release from the existing ranker. Grabs go through the same `ReleaseService` as manual grabs.
- **Duplicate-grab prevention:** a second grab for the same movie or episode is refused while one is active. Info-hash dedupe still applies.
- **Bounded runs:** at most 10 items per pass (`SearchPolicy.MaxSearchesPerRun`).
- **Backoff and retry:** no acceptable release waits 6 hours. Indexer or download-client failures back off 15 min, doubling to a 24 h cap. A grab waits 24 h before the item is looked at again.
- **Persistent state:** `SearchAttempt` rows survive restarts (migration `AddSearchAttempts`). Existing migrations are unchanged.
- **Endpoints:** `POST /api/automation/wanted-search` (manual run, shares the scheduler's lock) and `GET /api/automation/search-attempts`.

## Filesystem containment (added in this run)

Covered by `FilesystemContainmentTests`:
sibling directories that share a name prefix (`torrents` vs `torrents-evil`), traversal in source and destination,
symlinked directories in the source and destination trees, symlinked files as source, pre-existing symlinks at the
destination (never followed), dangling symlinks (cannot create a file outside the root), dry-run still refusing symlinks.

Symlink cases return early on hosts that cannot create symlinks (Windows without the privilege). Those cases are **not
verified** on such hosts. Generic reparse points are allowed on purpose, so OneDrive placeholder files still import.

## Integration tests (fakes only)

`EndToEndTests.MissingMovieIsSearchedSubmittedCompletedAndImported` runs the full chain: wanted detection, scheduled
Torznab search (GET only), accepted release, qBittorrent submission with category, downloading then completed,
safe import into the media root, no second search after import, idempotent re-import, and no delete or pause calls.

Also covered: episode targeting (only the matching episode is grabbed and imported into `Season 01`), dry-run changing
nothing, and the scheduler lock preventing overlapping search runs.

The download is simulated by writing a file into the temporary torrent root. Nothing is fetched. `RoutingHandler`
throws for any host not explicitly routed, so tests cannot reach the network or a real indexer or client.

## Not verified / not implemented

- **Real Torznab indexers, Prowlarr, real qBittorrent, real TMDb: NOT TESTED.** Only fake servers were used.
- **Upgrades** (replacing a file below the quality cutoff) are not searched. Only missing items are.
- **Missing-status jobs** (not found in the client for 15 minutes) do not block a new grab, by design. A stuck release could be grabbed again.
- **Season packs, specials, anime numbering, subtitles, music, books, requests:** out of scope.
- **Windows:** the test suite was run on Windows earlier (59 backend tests); the new containment and search tests have
  not been run on Windows yet. Symlink tests may return early there.
- **Container networking inside the sandbox:** verified with the CA workaround only (see caveat above).
- **Container as the non-root user with a bind-mounted host folder:** not tested. The verified run used a named volume.

## Next highest-value change

Run the wanted search against a real Torznab indexer and a real qBittorrent instance on a staging host, with dry-run
imports and a synthetic `/data`, and record the results here. Then add upgrade searching for items below the cutoff.
