# OneArr


### Tracker catalog and direct RSS

**RSS sources do not require an API key.** In Settings → Indexers choose
**Add RSS feed**, enter a name and a full HTTP(S) RSS/Atom URL, then Test and
Save. OneArr reads its latest entries, extracts torrent links or magnets and
size/seed/hash metadata if supplied, and filters matches locally. Because
many feeds publish *only the newest uploads*, RSS does not supply historical
search and OneArr cannot find older releases omitted by the feed.

For **Nyaa.si**, the integrated site adapter is preferable: select Nyaa from
the catalog, or enter a URL such as `https://nyaa.si/?page=rss&c=1_2&f=2`
in the standard indexer form. OneArr uses Nyaa's RSS search query and
preserves category/trusted filters. No key or username is needed. Nyaa
reports names, sizes, seeders and info hashes used by the release ranker.
For generic RSS sources, use **Add RSS feed** rather than pasting the URL
into a Torznab indexer; those endpoints don't support `api?t=caps`.

Feeds are read-only until a user grabs an eligible release (or existing
automatic-grab settings permit it). XML parsing prohibits DTD resolution
and caps download size. Existing qBittorrent categories and monitored-state
controls are unchanged.


Settings → Indexers → **Browse tracker catalog** lists known tracker definitions from
[Prowlarr/Indexers](https://github.com/Prowlarr/Indexers), with offline search and
filters for privacy, language, movies and TV. The metadata-only manifest can be
refreshed in the UI and is regenerated weekly in CI. These entries are **not**
all configured or supported indexers: a site definition is different from a
Torznab/Newznab API.

The native **Nyaa.si** RSS adapter is supported: select Nyaa.si and choose
**Add Nyaa (native RSS)** to fill its public RSS address, then Test and Save.
This performs a read-only RSS test; searches use Nyaa's RSS query and do not
require Prowlarr, Jackett, Docker or a site account. You can still configure
existing Torznab/Newznab feeds manually, including their API keys.

**TorrentLeech** has a native adapter. Select it from the catalog, choose
**Add TorrentLeech (native)**, and enter the website username/password plus an
optional *Alt 2FA Token* from your profile. Test signs in and verifies the
account session; Save stores the credentials as encrypted data in OneArr's
database. The `config/secret.key` file is required to decrypt them when
reinstalling or restoring backups. Keep that file private and preserve it.

Native TorrentLeech searches read its JSON torrent listings. Downloaded
`.torrent` files are fetched by OneArr with the authenticated session and
submitted directly to qBittorrent as multipart bytes; OneArr never gives
qBittorrent the website session cookie. This is distinct from entering a
private tracker login in the generic Torznab API-key box.

Other entries still show their required configuration labels for reference
and are **catalog-only** until their native adapters exist; no private
credentials are collected for them. Full Cardigann engine coverage across
the directory remains tracked in [issue #14](https://github.com/drathaxie/OneArr/issues/14).
No website login is guaranteed to succeed when a tracker is down or enforces
an anti-bot challenge.


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

## Automatic completed-download handling (opt-in)

OneArr can now automatically **import completed qBittorrent downloads** without
a manual approval click, but only when explicitly enabled. The default is OFF.
This setting is separate from automatic grabbing:

1. Configure and verify torrent/download roots and media roots.
2. In **Settings → Media Management → Import paths**, turn **dry-run OFF** only
   after confirming the paths are correct.
3. In **Settings → Automation → Completed-download importing**, turn on the
   dedicated automatic-import toggle and save.
4. If desired, enable renaming in **Media Management → Naming**. Otherwise the
   original release filename is retained.

The scheduler checks every five minutes, after syncing qBittorrent. To qualify,
a download must be 100% complete, owned by an enabled/reachable qBittorrent
client, correctly categorized, recently observed, finished long enough for the
file to settle, and confidently matched to a media item. Any uncertain match,
multiple movie files, unmatched/multi-episode TV item, existing movie file,
destination collision, stale client, changed source or inaccessible path
requires **manual preview and confirmation** instead. Every automatic import
passes the same stored preview and fingerprint validation as manual import.
Hardlinks preserve seeding; verified copies are used across volumes. No source
file is moved or deleted. No automatic upgrades or torrent cleanup occur.

**Safety switches:** toggling automatic import off stops subsequent jobs;
`import.dryRun=true` blocks all automatic physical imports regardless of
the automation setting. Enabling automatic grabbing does not enable imports
and enabling imports does not enable grabbing.

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

## Docker (optional, not the v0.3.0 Windows path)

The Windows release above is the supported path for v0.3.0 and needs no Docker, WSL, Git, Node.js, or .NET SDK. Docker remains an optional, unverified developer path only:

```powershell
Copy-Item .env.example .env
docker compose up --build
```

The Dockerfile is multi-stage (Node build, .NET publish, non-root ASP.NET runtime). It was built and started in a development sandbox, with `/health`, the UI, auth and restart persistence checked. That build needed a sandbox-specific CA workaround for package downloads; a normal host does not. The container's `/config` must be writable by UID 1654 (the `app` user in the .NET runtime image). Bind-mounted host folders with that ownership were not tested, and the Windows release, upgrade, and rollback behaviour in `tools/windows/INSTALL.txt` does not apply to the container.

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
src/OneArr.Application     contracts (IMetadataProvider, IIndexerClient, IDownloadClient, IReleaseParser, IReleaseRanker, IImporter, ILegacyArrReader), release parser, ranker
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
