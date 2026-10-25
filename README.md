# OneArr

## Windows (recommended)

Install from a release. You need nothing else installed: no Docker, WSL, Git, Node.js or .NET SDK.

1. Download `OneArr-<version>-win-x64.zip` from the GitHub Releases page and check its `.sha256` file.
2. Extract the zip and run `OneArr.Tray.exe`. It installs to `%LOCALAPPDATA%\OneArr`, starts OneArr in the background, and adds a notification-area icon.
3. Open http://127.0.0.1:8780 and create your password.

See `INSTALL.txt` inside the zip for where data lives, how updates and rollbacks work, and how to uninstall.

## Building from source (developers)

Only needed to change OneArr itself. Requires the .NET 10 SDK and Node.js 22.

```powershell
cd web; npm ci; npm run build; cd ..
dotnet test OneArr.slnx -c Release --filter "Platform!=Windows"
dotnet test OneArr.slnx -c Release --filter "Platform=Windows"   # Windows only
```

Releases are built by `.github/workflows/release.yml` on a Windows runner, after all tests and the acceptance script pass.

A self-hosted movie and TV manager built as one ASP.NET Core 10 process with a React + TypeScript UI, SQLite storage in `/config`, and one web port (`8780`).

**Status: vertical slice, not a Sonarr/Radarr replacement yet.** See `PROGRESS.md` for exactly what is verified and what is not. Do not point OneArr at production media until you have read `docs/MIGRATION.md`.

## What runs

- First-run password setup, bearer-token sign-in, `/health`.
- TMDb search and add (movies and series), stable `(MediaType, Provider, ProviderId)` identity, de-duplicated re-adds, seasons and episodes stored, monitoring toggles, per-item root folder and quality profile.
- Quality profiles (minimum and cutoff resolution, web-source toggle).
- Generic Torznab indexers: manual search with accept/reject reasons per release.
- Automatic grabbing (off by default): every 15 minutes, searches wanted movies and aired episodes, and grabs the best accepted release. Bounded to 10 items per pass, with backoff on failures and no second grab while a download is active. Turn it on with `PUT /api/settings` and `{"automaticGrabbing": true}` (the UI toggle is not built yet), or trigger a run with `POST /api/automation/wanted-search`.
- qBittorrent WebUI v2 client: cookie login with re-login on 403, add magnet to a configured category (`onearr-movies` / `onearr-tv`), queue state sync.
- Safe importer: canonical-path containment, symlink refusal, no overwrite, idempotent same-content reimport, hardlink on same filesystem, otherwise verified copy (SHA-256). Source files are never deleted. Dry-run is the default.
- Read-only library scan of the media root.
- Symlinks anywhere below the torrent or media root cause the import to fail closed.
- Migration preview from Sonarr v3 (`/api/v3/series`) and Radarr v3 (`/api/v3/movie`), GET-only, followed by explicit confirmation to copy selected entries into OneArr's own database.

## Requirements

- .NET SDK 10.0 (`dotnet --info`)
- Node.js 22+ and npm
- Python 3 (only for the local fake-service smoke test)
- Docker (optional, for the container path)

## Build and run from a fresh clone

Commands below are for **Windows PowerShell 5.1 or PowerShell 7**. PowerShell 5.1 does not accept `&&`, so run each line separately (or use `;`). The `VAR=value command` form does not work in PowerShell; use `$env:VAR = "value"` instead.

```powershell
# 1. Frontend: compile into src\OneArr.Api\wwwroot (served by ASP.NET Core)
cd web
npm ci
npm run build
cd ..

# 2. Backend: restore, build, test
dotnet restore OneArr.slnx
dotnet build OneArr.slnx -c Release
dotnet test OneArr.slnx -c Release

# 3. Run (SQLite database and token key are created in .\config)
$env:ONEARR_CONFIG_DIR = ".\config"
dotnet run --project src\OneArr.Api
# open http://127.0.0.1:8780 and create the admin password (12+ characters)
```

macOS or Linux (bash, zsh):

```bash
(cd web && npm ci && npm run build)
dotnet restore OneArr.slnx && dotnet build OneArr.slnx -c Release && dotnet test OneArr.slnx -c Release
ONEARR_CONFIG_DIR=./config dotnet run --project src/OneArr.Api
```

Optional environment variables (see `.env.example`):

| Variable | Default | Purpose |
|---|---|---|
| `ONEARR_CONFIG_DIR` | `./config` (`/config` in the container) | Holds `onearr.db` and `secret.key` (mode 0600). |
| `ONEARR_PORT` | `8780` | Listening port. |
| `ONEARR_LISTEN_ADDRESS` | `127.0.0.1` | Interface to listen on. The container image uses `0.0.0.0`; compose publishes only on the host address set by `ONEARR_BIND`. |
| `ONEARR_TMDB_API_KEY` | empty | Optional TMDb key; can also be set in Settings. Never returned by the API. |
| `ONEARR_TMDB_BASE_URL` | TMDb production | Override for fixture/fake servers only. |

Without a TMDb key the app runs, and search shows a setup message instead of results.

## Docker (not verified in this environment)

```powershell
Copy-Item .env.example .env
docker compose up --build
```

The Dockerfile is multi-stage (Node build, .NET publish, non-root ASP.NET runtime). It was built and started in the development sandbox, with `/health`, the UI, auth and restart persistence checked. That build needed a sandbox-specific CA workaround for package downloads; a normal host does not. The container's `/config` must be writable by UID 1654 (the `app` user in the .NET runtime image). Bind-mounted host folders with that ownership were not tested.

## Tests and smoke

```powershell
dotnet test OneArr.slnx -c Release      # 88 backend tests (parsing, search, importer containment, end-to-end, settings API), no network
cd web
npm test                                # 8 frontend tests (Settings screen forms), no network
cd ..
```

`tools/smoke/smoke.sh` is a **bash** script. On Windows run it from Git Bash or WSL, and it also needs `jq` and `python3` on that shell's PATH. It starts the API and local fake TMDb/Torznab/qBittorrent/Radarr services and runs 27 checks. Use it with fixture credentials only; it never contacts real trackers, TMDb or qBittorrent.

```bash
bash tools/smoke/smoke.sh
```

## Security notes

- Service credentials (TMDb key, indexer keys, qBittorrent password, legacy API keys) are stored or used server-side only and are never returned to the browser or written to logs.
- Outbound URLs are administrator-configured, restricted to http/https, and must not embed credentials. Link-local metadata addresses are refused.
- Bind to loopback (the compose default). Put an authenticated TLS reverse proxy in front before exposing it to a LAN.
- The browser keeps its session token in `sessionStorage` (cleared when the tab closes).

## Repository layout

```
src/OneArr.Domain          entities, enums, settings keys
src/OneArr.Application     contracts (IMetadataProvider, IIndexerClient, IDownloadClient, IReleaseParser, IReleaseRanker, ILibraryScanner, IImporter, ILegacyArrReader), release parser, ranker
src/OneArr.Infrastructure  EF Core (SQLite + migrations), TMDb / Torznab / qBittorrent adapters, SafeImporter, scanner, legacy reader, services, background scheduler
src/OneArr.Api             ASP.NET Core host, /api endpoints, static UI
web/                       React + TypeScript + Vite UI
tests/OneArr.Tests         xUnit tests and fixtures
tools/fake-services        standard-library fake upstream services
tools/smoke/smoke.sh       end-to-end smoke test
docs/MIGRATION.md          migration preview and rollback procedure
UPSTREAM.md                upstream references and dependency/licence inventory
```

## Licence

GPL-3.0-only (see `LICENSE`). No Sonarr, Radarr, Prowlarr or other upstream source code is included; see `UPSTREAM.md`.
