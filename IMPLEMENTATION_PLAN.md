# Implementation plan (OneArr movies + TV vertical slice)

1. Scaffold solution: Domain, Application, Infrastructure, Api, xUnit tests. Verify `dotnet restore`.
2. Domain entities and EF Core SQLite schema with unique `(MediaType, Provider, ProviderId)`; initial migration; auto-migrate at startup.
3. Application contracts, deterministic release parser and ranker with rejection reasons.
4. Outbound HTTP: named HttpClients, timeouts, bounded retry with Retry-After, URL validation, secret redaction.
5. Adapters: TMDb (fixtures and fake server), Torznab RSS, qBittorrent API v2 (cookie session, category, state mapping).
6. Catalog service (dedupe, seasons and episodes, monitoring), release search and manual grab, queue sync.
7. SafeImporter: containment, symlink refusal, no overwrite, idempotency, hardlink-then-verified-copy, dry-run.
8. Read-only Sonarr and Radarr reader, with preview and confirmed import.
9. Bounded background scheduler using per-job locks and job history.
10. Minimal responsive React UI: setup and login, Dashboard, Movies, Series, Wanted, Queue, Settings, Migration Preview.
11. Tests (xUnit), fake upstream services and end-to-end smoke script, documentation, licence and inventory.
12. Verify build, test and UI load; record unverified items (Docker, live services) in PROGRESS.md.
