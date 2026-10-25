# PROGRESS

Everything marked VERIFIED was executed in this environment. Everything else is labelled.

## Verified (commands executed, results observed)

| Item | Command | Result |
|---|---|---|
| .NET SDK 10.0.402 installed user-locally | `dotnet --info` | OK |
| Restore | `dotnet restore OneArr.slnx` | OK |
| Backend build | `dotnet build src/OneArr.Api` | 0 errors |
| Unit and integration tests | `dotnet test tests/OneArr.Tests` | 53 passed, 0 failed |
| Frontend typecheck and production build | `npx tsc --noEmit && npx vite build` (in `web/`) | OK, output to `src/OneArr.Api/wwwroot` |
| EF Core initial migration | `dotnet-ef migrations add InitialCreate` | created `20261025023026_InitialCreate` |
| End-to-end smoke against fake services | `bash tools/smoke/smoke.sh` | 27 of 27 checks passed |
| Headless browser load at 390px | Playwright script | setup form renders, all 7 views navigate, no horizontal overflow, 0 console errors |

Smoke coverage: health; auth required; first-run setup; second setup refused; wrong password refused; secrets not returned; TMDb search and add; re-add dedupe; release decisions with reasons; server refuses rejected grab; manual grab to qBittorrent with hash and category; duplicate grab refused; queue sync reads state; dry-run writes nothing; real import into the item's root folder; source retained; job marked Imported; import idempotent; legacy preview; legacy reader GET-only; dashboard counts; no qBittorrent delete call.

## Not verified / not done

- **Docker build and `docker compose up`: UNVERIFIED.** The Docker CLI exists but the daemon socket was absent.
- **Live services: UNVERIFIED.** No real TMDb, indexer, qBittorrent, Sonarr or Radarr was contacted, by design.
- **Fresh clone build: partially verified.** Restore, build and tests ran on Linux in the working tree; a clean clone and `npm ci` were not run separately.
- **Windows: NOT VERIFIED by this environment.** The build commands were corrected for PowerShell. The smoke script is bash-only (run it from Git Bash or WSL). One symlink-refusal test returns early when symlink creation is not permitted, so that case is not verified on such Windows hosts. The importer hardlink path is Linux-only; Windows uses verified copy.
- **Automatic grabbing and wanted-search automation: NOT IMPLEMENTED.** Manual only. The background scheduler runs queue sync and import only.
- **Prowlarr bridge, Cardigann, Usenet, music, books, subtitles, requests: NOT IMPLEMENTED** (out of scope for this run).
- **Fail-closed root-folder check** (item root outside the media root) is implemented in `DownloadService` but has no dedicated automated test; it is covered by code review only.
- **Frontend unit tests: none.** The frontend is verified by typecheck, build and one headless load.
- **Sonarr/Radarr response shapes** were written from public API knowledge and tested against fixtures, not against pinned upstream versions.
- **Season selection** in the add form: all seasons are monitored; per-season selection is accepted by the API but not exposed in the UI.
- **Import of multi-file season packs** is untested beyond the single-file path.

## Outstanding risks

- The browser session token lives in `sessionStorage`; a stolen token grants access until it expires (7 days). Deleting `secret.key` and restarting invalidates all tokens.
- `ONEARR_TMDB_BASE_URL` is intended only for fixtures and should not be set in production.
- Hardlink uses libc `link()` via P/Invoke, so it is Linux-only; other platforms fall back to verified copy.
- A single OneArr process per `/config` is assumed; multiple instances sharing one SQLite file are not supported.

## Next highest-value change

Run `docker compose up --build` on a host with a working Docker daemon, then point the smoke test's fake services at the containerised app. Add a dedicated test for the root-folder containment failure path.
