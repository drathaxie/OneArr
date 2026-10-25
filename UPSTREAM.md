# Upstream references and dependency inventory

## Upstream projects consulted

No upstream source repository was cloned, read in bulk, vendored or compiled into OneArr in this run. Behaviour was designed from public documentation and the research in `ONEARR_RESEARCH_AND_PLAN.md`. Because no upstream code was consumed, **no upstream commit SHAs are pinned**. If a future change copies or ports GPL code from Sonarr, Radarr, Prowlarr or others, that change must record the repository URL, commit SHA, licence and the file headers here.

| Project | Used for | Code copied? |
|---|---|---|
| Sonarr v3 API (`/api/v3/series`) | Read-only migration field names (`title`, `year`, `tmdbId`, `tvdbId`, `imdbId`, `path`, `monitored`) | No |
| Radarr v3 API (`/api/v3/movie`) | Same as above | No |
| Torznab / Newznab feed format | RSS item fields and `torznab:attr` names | No |
| qBittorrent WebUI API v2 | Login, `torrents/add`, `torrents/info`, state names | No |
| TMDb API v3 | Search, movie and TV details, season and episode endpoints | No |

## Dependency licence inventory

NuGet (direct references):

| Package | Version | Licence |
|---|---|---|
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.13 | MIT |
| Microsoft.EntityFrameworkCore.Design | 10.0.13 | MIT |
| Microsoft.Data.Sqlite | (transitive via EF Core) | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| coverlet.collector | 6.0.4 | MIT |

npm (`web/`):

| Package | Version | Licence |
|---|---|---|
| react, react-dom | 18.3.1 | MIT |
| vite | 5.4.11 | MIT |
| @vitejs/plugin-react | 4.3.4 | MIT |
| typescript | 5.6.3 | Apache-2.0 |
| @types/react, @types/react-dom | 18.3.12, 18.3.1 | MIT |

Licence values reflect each package's published licence as understood at the time of writing; they were not re-read from package metadata in this run. Re-check with `dotnet list package --include-transitive` and `npm ls --all` before any distribution.

## Distribution

OneArr itself is GPL-3.0-only (see `LICENSE`, the GNU GPL v3 text from the system `common-licenses` file). Running it privately has no source-disclosure obligation. Conveying binaries to others requires making corresponding source available under the GPL. This is engineering guidance, not legal advice.
