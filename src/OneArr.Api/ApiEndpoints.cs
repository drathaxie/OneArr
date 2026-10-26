using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure;
using OneArr.Infrastructure.Files;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Library;
using OneArr.Infrastructure.Legacy;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Security;
using OneArr.Infrastructure.Services;

namespace OneArr.Api;

public static class ApiEndpoints
{
    public sealed record SetupRequest(string Password);
    public sealed record AddMediaBody(string Type, string ProviderId, int QualityProfileId, string? RootFolder, bool Monitored, List<int>? Seasons, bool? AllSeasons);
    public sealed record PatchMediaBody(bool? Monitored, int? QualityProfileId, string? RootFolder);
    public sealed record SearchBody(int MediaItemId, int? EpisodeId);
    public sealed record GrabBody(int MediaItemId, int? EpisodeId, string Title, string Link, string? InfoHash);
    public sealed record IndexerBody(string Name, string BaseUrl, string ApiKey, bool Enabled);
    public sealed record ClientBody(string Name, string BaseUrl, string Username, string Password, string? MovieCategory, string? TvCategory, bool Enabled);
    public sealed record SettingsBody(string? TmdbApiKey, bool? DryRunImport, string? TorrentRoot, string? MediaRoot, bool? AutomaticGrabbing = null);
    public sealed record ProfileBody(string Name, int MinResolution, int CutoffResolution, bool AllowWebSources);
    public sealed record MigrationPreviewBody(string Kind, string BaseUrl, string ApiKey);
    public sealed record MigrationImportBody(List<MigrationSelection> Items, int QualityProfileId, string RootFolder);
    public sealed record SeasonSelection(int SeasonNumber, bool Monitored);
    public sealed record EpisodeSelection(int SeasonNumber, int EpisodeNumber, bool Monitored);
    public sealed record MigrationSelection(string Type, int? TmdbId, string Title, bool Monitored, string? LegacyPath = null, string? LegacySource = null, List<SeasonSelection>? Seasons = null, List<EpisodeSelection>? Episodes = null, bool EpisodesUnknown = false);
    public sealed record MonitoringSyncPreview(List<MonitoringChange> Changes, List<string> UnmatchedTitles, int SkippedNoTmdb, int ItemsChecked, List<string> EpisodesUnknownTitles);
    public sealed record MonitoringSyncBody(string Kind, string BaseUrl, string ApiKey, bool? Apply = null, MonitoringSyncPreview? Preview = null);
    public sealed record LibraryRootBody(string Type, string Path, string? Label);
    public sealed record PathMappingBody(string Type, string SourcePrefix, string TargetPrefix);
    public sealed record ApplyBody(List<string>? ExcludePaths, bool? AcknowledgeUnmatched);
    public sealed record BulkRootBody(string MediaType, string? FromRootFolder, string ToRootFolder, List<int>? Ids);
    public sealed record ConnectionTestBody(string Service, int? IndexerId = null, int? ClientId = null, string? Kind = null, string? BaseUrl = null, string? ApiKey = null);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");
        var tokens = app.Services.GetRequiredService<TokenService>();

        api.MapGet("/auth/status", async (IDbContextFactory<OneArrDbContext> db, HttpContext ctx) =>
        {
            await using var c = await db.CreateDbContextAsync();
            var configured = await c.Settings.AnyAsync(s => s.Key == SettingKeys.AdminPasswordHash);
            var header = ctx.Request.Headers.Authorization.ToString();
            var authed = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && tokens.Validate(header["Bearer ".Length..], DateTimeOffset.UtcNow);
            return Results.Ok(new { configured, authenticated = authed });
        });

        api.MapPost("/auth/setup", async (SetupRequest body, IDbContextFactory<OneArrDbContext> db) =>
        {
            if (body.Password is null || body.Password.Length < 12) return Results.BadRequest(new { error = "Password must be at least 12 characters" });
            await using var c = await db.CreateDbContextAsync();
            if (await c.Settings.AnyAsync(s => s.Key == SettingKeys.AdminPasswordHash)) return Results.Conflict(new { error = "Already set up" });
            c.Settings.Add(new AppSetting { Key = SettingKeys.AdminPasswordHash, Value = PasswordHasher.Hash(body.Password) });
            await c.SaveChangesAsync();
            var (token, expires) = tokens.Issue(DateTimeOffset.UtcNow);
            return Results.Ok(new { token, expiresAt = expires });
        });

        api.MapPost("/auth/login", async (SetupRequest body, IDbContextFactory<OneArrDbContext> db) =>
        {
            await using var c = await db.CreateDbContextAsync();
            var hash = (await c.Settings.FindAsync(SettingKeys.AdminPasswordHash))?.Value;
            if (hash is null) return Results.Conflict(new { error = "Run first-time setup before signing in" });
            if (!PasswordHasher.Verify(body.Password ?? "", hash)) return Results.Json(new { error = "Incorrect password" }, statusCode: 401);
            var (token, expires) = tokens.Issue(DateTimeOffset.UtcNow);
            return Results.Ok(new { token, expiresAt = expires });
        });

        api.MapGet("/dashboard", async (IDbContextFactory<OneArrDbContext> db, MediaService media, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var wanted = await media.WantedAsync(ct);
            return Results.Ok(new
            {
                movies = await c.MediaItems.CountAsync(m => m.MediaType == MediaType.Movie, ct),
                series = await c.MediaItems.CountAsync(m => m.MediaType == MediaType.Series, ct),
                wantedMovies = wanted.Movies.Count,
                wantedEpisodes = wanted.Episodes.Count,
                activeDownloads = await c.DownloadJobs.CountAsync(j => j.Status == DownloadStatus.Queued || j.Status == DownloadStatus.Downloading, ct),
                recentJobs = await c.JobHistories.OrderByDescending(h => h.Id).Take(8).Select(h => new { h.Name, h.StartedAt, h.FinishedAt, h.Success, h.Message }).ToListAsync(ct),
                tmdbConfigured = await c.Settings.AnyAsync(s => s.Key == SettingKeys.TmdbApiKey, ct) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ONEARR_TMDB_API_KEY")),
            });
        });

        api.MapGet("/quality-profiles", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            return Results.Ok(await c.QualityProfiles.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct));
        });

        api.MapPost("/quality-profiles", async (ProfileBody body, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            if (body.MinResolution is not (480 or 720 or 1080 or 2160) || body.CutoffResolution is not (480 or 720 or 1080 or 2160) || body.CutoffResolution < body.MinResolution)
                return Results.BadRequest(new { error = "Resolutions must be 480, 720, 1080 or 2160, with cutoff >= minimum" });
            await using var c = await db.CreateDbContextAsync(ct);
            var profile = new QualityProfile { Name = body.Name.Trim(), MinResolution = body.MinResolution, CutoffResolution = body.CutoffResolution, AllowWebSources = body.AllowWebSources };
            c.QualityProfiles.Add(profile);
            await c.SaveChangesAsync(ct);
            return Results.Created($"/api/quality-profiles/{profile.Id}", profile);
        });

        api.MapGet("/metadata/search", async (string q, string type, IMetadataProvider metadata, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length > 200) return Results.BadRequest(new { error = "Query must be 1-200 characters" });
            var mt = ParseType(type);
            return Results.Ok(await metadata.SearchAsync(mt, q.Trim(), ct));
        });

        api.MapGet("/media", async (string type, MediaService media, CancellationToken ct) =>
            Results.Ok(await media.ListAsync(ParseType(type), ct)));

        api.MapPost("/media", async (AddMediaBody body, MediaService media, SettingsStore settings, CancellationToken ct) =>
        {
            var root = body.RootFolder ?? await settings.GetAsync(SettingKeys.MediaRoot, ct) ?? "";
            var result = await media.AddAsync(new AddMediaRequest(ParseType(body.Type), body.ProviderId, body.QualityProfileId, root, body.Monitored, body.Seasons, body.AllSeasons ?? true), ct);
            return result.Created ? Results.Created($"/api/media/{result.Item.Id}", result) : Results.Ok(result);
        });

        api.MapGet("/media/{id:int}", async (int id, MediaService media, CancellationToken ct) =>
            await media.GetAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());

        // Read-only: linked files recorded in OneArr's own database by a library scan or an import.
        // This never scans the disk and never touches a file; it only reports what OneArr already knows.
        api.MapGet("/media/{id:int}/files", async (int id, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var item = await c.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (item is null) return Results.NotFound();
            var files = await c.MediaFiles.AsNoTracking().Where(f => f.MediaItemId == id).OrderBy(f => f.Path)
                .Select(f => new { f.Path, f.SizeBytes, f.Resolution, f.Status, f.MatchMethod }).ToListAsync(ct);
            return Results.Ok(new { itemId = item.Id, item.Title, item.RootFolder, item.HasFile, files });
        });

        api.MapPatch("/media/{id:int}", async (int id, PatchMediaBody body, MediaService media, CancellationToken ct) =>
            await media.UpdateAsync(id, body.Monitored, body.QualityProfileId, body.RootFolder, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPatch("/media/{id:int}/seasons/{season:int}", async (int id, int season, PatchMediaBody body, MediaService media, CancellationToken ct) =>
            body.Monitored is bool m && await media.UpdateSeasonMonitoredAsync(id, season, m, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPatch("/media/episodes/{episodeId:int}", async (int episodeId, PatchMediaBody body, MediaService media, CancellationToken ct) =>
            body.Monitored is bool m && await media.UpdateEpisodeMonitoredAsync(episodeId, m, ct) ? Results.NoContent() : Results.NotFound());

        // Detail view: seasons, episodes, linked files, and per-episode file resolution in one read.
        // Episode resolution comes from the linked file row, not from the filename, so a missing file has none.
        api.MapGet("/media/{id:int}/detail", async (int id, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var item = await c.MediaItems.AsNoTracking().Include(m => m.Seasons).Include(m => m.Episodes).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (item is null) return Results.NotFound();
            var files = await c.MediaFiles.AsNoTracking().Where(f => f.MediaItemId == id).OrderBy(f => f.Path)
                .Select(f => new { f.Path, f.SizeBytes, f.Resolution, f.Status, f.MatchMethod }).ToListAsync(ct);
            var episodeIds = item.Episodes.Select(e => e.Id).ToHashSet();
            var resolutions = await c.MediaFileEpisodes.AsNoTracking().Join(c.MediaFiles.AsNoTracking(), l => l.MediaFileId, f => f.Id,
                (l, f) => new { l.EpisodeId, f.Resolution }).Where(x => episodeIds.Contains(x.EpisodeId)).ToListAsync(ct);
            var resolutionByEpisode = resolutions.GroupBy(x => x.EpisodeId).ToDictionary(g => g.Key, g => g.First().Resolution);
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
            return Results.Ok(new
            {
                item.Id, item.MediaType, item.Title, item.Year, item.Overview, item.PosterPath, item.Monitored,
                item.QualityProfileId, item.RootFolder, item.HasFile, item.LegacyPath, item.LegacySource,
                seasons = item.Seasons.OrderBy(s => s.SeasonNumber).Select(s => new
                {
                    s.SeasonNumber, s.Monitored,
                    episodeCount = item.Episodes.Count(e => e.SeasonNumber == s.SeasonNumber),
                    hasFileCount = item.Episodes.Count(e => e.SeasonNumber == s.SeasonNumber && e.HasFile),
                    wantedCount = item.Episodes.Count(e => e.SeasonNumber == s.SeasonNumber && item.Monitored && e.Monitored && !e.HasFile && e.AirDate != null && e.AirDate <= today),
                }),
                episodes = item.Episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).Select(e => new
                {
                    e.Id, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirDate, e.Monitored, e.HasFile,
                    resolution = resolutionByEpisode.GetValueOrDefault(e.Id),
                }),
                files,
            });
        });

        api.MapDelete("/media/{id:int}", async (int id, MediaService media, CancellationToken ct) =>
            await media.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/wanted", async (MediaService media, CancellationToken ct) => Results.Ok(await media.WantedAsync(ct)));

        api.MapPost("/search", async (SearchBody body, ReleaseService releases, CancellationToken ct) =>
        {
            var result = await releases.SearchAsync(body.MediaItemId, body.EpisodeId, ct);
            return Results.Ok(new
            {
                indexerErrors = result.IndexerErrors,
                results = result.Decisions.Select(d => new
                {
                    title = d.Candidate.Title,
                    link = d.Candidate.Link,
                    infoHash = d.Candidate.InfoHash,
                    size = d.Candidate.Size,
                    seeders = d.Candidate.Seeders,
                    indexer = d.Candidate.Indexer,
                    resolution = d.Parsed.Resolution,
                    source = d.Parsed.Source,
                    accepted = d.Accepted,
                    score = d.Score,
                    reasons = d.Reasons,
                }),
            });
        });

        api.MapPost("/grab", async (GrabBody body, ReleaseService releases, CancellationToken ct) =>
        {
            var job = await releases.GrabAsync(new GrabRequest(body.MediaItemId, body.EpisodeId, body.Title, body.Link, body.InfoHash), ct);
            return Results.Created($"/api/queue/{job.Id}", job);
        });

        api.MapGet("/indexers", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            return Results.Ok(await c.Indexers.AsNoTracking().Select(i => new { i.Id, i.Name, i.BaseUrl, i.Enabled, hasKey = i.ApiKey.Length > 0 }).ToListAsync(ct));
        });

        api.MapPost("/indexers", async (IndexerBody body, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            UrlGuard.RequireServiceUrl(body.BaseUrl);
            await using var c = await db.CreateDbContextAsync(ct);
            c.Indexers.Add(new IndexerConfig { Name = body.Name.Trim(), BaseUrl = body.BaseUrl.Trim(), ApiKey = body.ApiKey, Enabled = body.Enabled });
            await c.SaveChangesAsync(ct);
            return Results.Created("/api/indexers", new { body.Name, body.BaseUrl, body.Enabled });
        });

        api.MapDelete("/indexers/{id:int}", async (int id, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var row = await c.Indexers.FindAsync([id], ct);
            if (row is null) return Results.NotFound();
            c.Indexers.Remove(row);
            await c.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/download-clients", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            return Results.Ok(await c.DownloadClients.AsNoTracking().Select(d => new { d.Id, d.Name, d.BaseUrl, d.Username, d.MovieCategory, d.TvCategory, d.Enabled, hasPassword = d.Password.Length > 0 }).ToListAsync(ct));
        });

        api.MapPost("/download-clients", async (ClientBody body, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            UrlGuard.RequireServiceUrl(body.BaseUrl);
            await using var c = await db.CreateDbContextAsync(ct);
            c.DownloadClients.Add(new DownloadClientConfig
            {
                Name = body.Name.Trim(),
                BaseUrl = body.BaseUrl.Trim(),
                Username = body.Username,
                Password = body.Password,
                MovieCategory = string.IsNullOrWhiteSpace(body.MovieCategory) ? "onearr-movies" : body.MovieCategory.Trim(),
                TvCategory = string.IsNullOrWhiteSpace(body.TvCategory) ? "onearr-tv" : body.TvCategory.Trim(),
                Enabled = body.Enabled,
            });
            await c.SaveChangesAsync(ct);
            return Results.Created("/api/download-clients", new { body.Name, body.BaseUrl, body.Enabled });
        });

        api.MapDelete("/download-clients/{id:int}", async (int id, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var row = await c.DownloadClients.FindAsync([id], ct);
            if (row is null) return Results.NotFound();
            c.DownloadClients.Remove(row);
            await c.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/queue", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            return Results.Ok(await c.DownloadJobs.AsNoTracking().OrderByDescending(j => j.Id).Take(200).ToListAsync(ct));
        });

        // Explicit stuck-download reconciliation: a Missing job blocks automatic and manual grabs
        // for its item until an operator clears it. Clearing only changes OneArr's record to Failed;
        // it never touches a torrent, a file, or qBittorrent.
        api.MapPost("/queue/{id:int}/clear-missing", async (int id, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var job = await c.DownloadJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null) return Results.NotFound();
            if (job.Status != DownloadStatus.Missing) return Results.BadRequest(new { error = "Only a download marked Missing can be cleared" });
            job.Status = DownloadStatus.Failed;
            job.Error = "Cleared as missing by operator";
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await c.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Missing download cleared; the item can be searched or grabbed again" });
        });

        api.MapPost("/queue/sync", async (DownloadService downloads, JobRunner runner, CancellationToken ct) =>
        {
            var ran = await runner.TryRunAsync("queue-sync", async t => $"Updated {await downloads.SyncQueueAsync(t)} job(s)", ct);
            return ran ? Results.Ok(new { status = "done" }) : Results.Conflict(new { error = "Queue sync already running" });
        });

        api.MapPost("/import", async (bool? dryRun, DownloadService downloads, SettingsStore settings, JobRunner runner, CancellationToken ct) =>
        {
            var dry = dryRun ?? await settings.GetBoolAsync(SettingKeys.DryRunImport, true, ct);
            string? message = null;
            var ran = await runner.TryRunAsync("import", async t =>
            {
                var summary = await downloads.ImportCompletedAsync(dry, t);
                message = summary.Message;
                return summary.Message;
            }, ct);
            return ran ? Results.Ok(new { dryRun = dry, message }) : Results.Conflict(new { error = "Import already running" });
        });

        api.MapGet("/imports", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            return Results.Ok(await c.ImportRecords.AsNoTracking().OrderByDescending(r => r.Id).Take(200).ToListAsync(ct));
        });

        api.MapGet("/library/status", async (LibraryReconciler library, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            return Results.Ok(new
            {
                windows = OperatingSystem.IsWindows(),
                automation = await library.CheckAutomationAsync(ct),
                roots = await c.LibraryRoots.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct),
                mappings = await c.PathMappings.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct),
            });
        });

        // Removes OneArr's record of the folder only. No file in it is touched.
        api.MapPost("/library/roots", async (LibraryRootBody body, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            var type = ParseType(body.Type);
            var windows = OperatingSystem.IsWindows();
            var problem = LibraryPaths.ValidateRoot(body.Path, windows);
            if (problem is not null) return Results.BadRequest(new { error = problem });

            var path = body.Path.Trim();
            var key = LibraryPaths.Key(path, windows);
            await using var c = await db.CreateDbContextAsync(ct);
            if (await c.LibraryRoots.AnyAsync(r => r.MediaType == type && r.PathKey == key, ct))
                return Results.Conflict(new { error = "That folder is already a library root for this media type" });

            var root = new LibraryRoot { MediaType = type, Path = path, PathKey = key, Label = string.IsNullOrWhiteSpace(body.Label) ? null : body.Label.Trim() };
            c.LibraryRoots.Add(root);
            await c.SaveChangesAsync(ct);
            return Results.Ok(root);
        });

        // Live reachability check for one configured folder. Read-only: it does not scan, does not
        // change the stored last-scan state, and does not touch any file in the folder.
        api.MapPost("/library/roots/{id:int}/test", async (int id, ILibraryFileSystem fs, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var root = await c.LibraryRoots.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (root is null) return Results.NotFound();
            var check = await fs.CheckRootAsync(root.Path, ct);
            return Results.Ok(new { root.Id, root.Path, state = check.State.ToString(), check.Message });
        });

        api.MapDelete("/library/roots/{id:int}", async (int id, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var root = await c.LibraryRoots.FindAsync([id], ct);
            if (root is null) return Results.NotFound();
            c.LibraryRoots.Remove(root);
            await c.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Replaces the full mapping list. Mappings translate Sonarr/Radarr paths for this PC; they never change files.
        api.MapPut("/library/mappings", async (List<PathMappingBody> body, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            var windows = OperatingSystem.IsWindows();
            var rows = new List<PathMapping>();
            foreach (var m in body.Take(100))
            {
                var type = ParseType(m.Type);
                var problem = LibraryPaths.ValidateMapping(m.SourcePrefix, m.TargetPrefix, windows);
                if (problem is not null) return Results.BadRequest(new { error = problem, source = m.SourcePrefix });
                if (rows.Any(r => r.MediaType == type && r.SourcePrefix == m.SourcePrefix.Trim()))
                    return Results.BadRequest(new { error = $"Two mappings use the same source path: {m.SourcePrefix}" });
                rows.Add(new PathMapping { MediaType = type, SourcePrefix = m.SourcePrefix.Trim(), TargetPrefix = m.TargetPrefix.Trim() });
            }

            await using var c = await db.CreateDbContextAsync(ct);
            await c.PathMappings.ExecuteDeleteAsync(ct);
            c.PathMappings.AddRange(rows);
            await c.SaveChangesAsync(ct);
            return Results.Ok(rows);
        });

        // Suggests mappings from the stored Sonarr/Radarr paths, and counts catalog entries that still lack one.
        api.MapGet("/library/mapping-suggestions", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var items = await c.MediaItems.AsNoTracking().Select(m => new { m.MediaType, m.LegacyPath, m.RootFolder }).ToListAsync(ct);
            var suggestions = items.Where(i => i.LegacyPath is { Length: > 0 })
                .Select(i => new { Type = i.MediaType, Parent = ParentFolder(i.LegacyPath!) })
                .Where(x => x.Parent is { Length: > 0 })
                .GroupBy(x => new { x.Type, x.Parent })
                .Select(g => new { mediaType = g.Key.Type.ToString(), sourcePrefix = g.Key.Parent, count = g.Count() })
                .OrderByDescending(g => g.count).ToList();
            var rootFolders = items.GroupBy(i => i.RootFolder)
                .Select(g => new { rootFolder = g.Key, count = g.Count() }).OrderByDescending(g => g.count).ToList();
            return Results.Ok(new
            {
                suggestions,
                entriesWithoutLegacyPath = items.Count(i => string.IsNullOrEmpty(i.LegacyPath)),
                rootFolders,
            });
        });

        // Moves catalog entries to a new RootFolder. It changes only where future downloads go, never files on disk.
        api.MapPost("/media/root-folder", async (BulkRootBody body, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            var type = ParseType(body.MediaType);
            MediaService.ValidateRoot(body.ToRootFolder);
            var target = body.ToRootFolder.Trim();
            await using var c = await db.CreateDbContextAsync(ct);
            var query = c.MediaItems.Where(m => m.MediaType == type);
            if (body.Ids is { Count: > 0 }) query = query.Where(m => body.Ids.Contains(m.Id));
            else if (!string.IsNullOrWhiteSpace(body.FromRootFolder)) query = query.Where(m => m.RootFolder == body.FromRootFolder.Trim());
            else return Results.BadRequest(new { error = "Choose the entries by id or by their current root folder" });
            var updated = await query.Where(m => m.RootFolder != target).ExecuteUpdateAsync(s => s.SetProperty(m => m.RootFolder, target), ct);
            return Results.Ok(new { updated });
        });

        // Read-only preview. It stores the reviewed result and changes no media rows or files.
        api.MapPost("/library/preview", async (LibraryReconciler library, CancellationToken ct) => Results.Ok(await library.PreviewAsync(ct)));

        api.MapPost("/library/plans/{id:int}/apply", async (int id, ApplyBody? body, LibraryReconciler library, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await library.ApplyAsync(id, body?.ExcludePaths, body?.AcknowledgeUnmatched ?? false, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        api.MapPost("/automation/wanted-search", async (WantedSearchService search, JobRunner runner, CancellationToken ct) =>
        {
            string? message = null;
            var ran = await runner.TryRunAsync("wanted-search", async t =>
            {
                var summary = await search.RunOnceAsync(t);
                message = summary.ToString();
                return message;
            }, ct);
            return ran ? Results.Ok(new { message }) : Results.Conflict(new { error = "A wanted search is already running" });
        });

        api.MapGet("/automation/search-attempts", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            return Results.Ok(await c.SearchAttempts.AsNoTracking().OrderBy(s => s.NextAttemptAt).Take(200).ToListAsync(ct));
        });

        api.MapGet("/jobs", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            return Results.Ok(await c.JobHistories.AsNoTracking().OrderByDescending(h => h.Id).Take(100).ToListAsync(ct));
        });

        api.MapGet("/settings", async (SettingsStore settings, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var tmdbStored = await c.Settings.AnyAsync(s => s.Key == SettingKeys.TmdbApiKey, ct);
            return Results.Ok(new
            {
                tmdbConfigured = tmdbStored || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ONEARR_TMDB_API_KEY")),
                dryRunImport = await settings.GetBoolAsync(SettingKeys.DryRunImport, true, ct),
                torrentRoot = await settings.GetAsync(SettingKeys.TorrentRoot, ct),
                mediaRoot = await settings.GetAsync(SettingKeys.MediaRoot, ct),
                automaticGrabbing = await settings.GetBoolAsync(SettingKeys.AutomaticGrab, false, ct),
            });
        });

        api.MapPut("/settings", async (SettingsBody body, SettingsStore settings, CancellationToken ct) =>
        {
            if (body.TmdbApiKey is not null) await settings.SetAsync(SettingKeys.TmdbApiKey, string.IsNullOrWhiteSpace(body.TmdbApiKey) ? null : body.TmdbApiKey.Trim(), ct);
            if (body.DryRunImport is bool dry) await settings.SetAsync(SettingKeys.DryRunImport, dry.ToString(), ct);
            if (body.TorrentRoot is not null) await settings.SetAsync(SettingKeys.TorrentRoot, RequireAbsolute(body.TorrentRoot), ct);
            if (body.MediaRoot is not null) await settings.SetAsync(SettingKeys.MediaRoot, RequireAbsolute(body.MediaRoot), ct);
            if (body.AutomaticGrabbing is bool auto) await settings.SetAsync(SettingKeys.AutomaticGrab, auto.ToString(), ct);
            return Results.NoContent();
        });

        // Non-destructive connection tests. Nothing is added, downloaded, imported, or written to a
        // legacy application. Stored keys/passwords are used server-side only and are never returned;
        // a body-supplied legacy key is used for this one test and is never stored.
        api.MapPost("/connections/test", async (ConnectionTestBody body, IDbContextFactory<OneArrDbContext> db,
            IMetadataProvider metadata, IDownloadClient downloadClient,
            ILegacyArrReader legacyReader, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var service = (body.Service ?? "").Trim().ToLowerInvariant();
            try
            {
                await using var c = await db.CreateDbContextAsync(ct);
                switch (service)
                {
                    case "tmdb":
                    {
                        var results = await metadata.SearchAsync(MediaType.Movie, "test", ct);
                        return Results.Ok(new { ok = true, service, detail = $"TMDb answered a test search with {results.Count} result(s)." });
                    }
                    case "torznab":
                    {
                        if (body.IndexerId is not int indexerId) return Results.BadRequest(new { error = "indexerId is required" });
                        var indexer = await c.Indexers.AsNoTracking().FirstOrDefaultAsync(i => i.Id == indexerId, ct);
                        if (indexer is null) return Results.NotFound();
                        if (!indexer.Enabled) return Results.Ok(new { ok = false, service, detail = $"Indexer '{indexer.Name}' is disabled." });
                        var baseUri = UrlGuard.RequireServiceUrl(indexer.BaseUrl);
                        var http = httpFactory.CreateClient(OutboundHttp.TorznabClient);
                        http.BaseAddress = new Uri(baseUri, "/");
                        using var response = await OutboundHttp.SendWithRetryAsync(http,
                            () => new HttpRequestMessage(HttpMethod.Get, "api?t=caps&apikey=" + Uri.EscapeDataString(indexer.ApiKey)), ct);
                        if (!response.IsSuccessStatusCode)
                            return Results.Ok(new { ok = false, service, detail = $"Indexer '{indexer.Name}' returned HTTP {(int)response.StatusCode}." });
                        var caps = TorznabClient.ParseCaps(await response.Content.ReadAsStringAsync(ct), indexer.Name);
                        return Results.Ok(new
                        {
                            ok = true, service, name = indexer.Name,
                            detail = $"Indexer '{indexer.Name}' answered caps with {caps.Categories.Count} categor{(caps.Categories.Count == 1 ? "y" : "ies")}.",
                            categories = caps.Categories.Select(x => new { x.Id, x.Name }),
                            searchAvailable = caps.SearchAvailable, tvSearchAvailable = caps.TvSearchAvailable, movieSearchAvailable = caps.MovieSearchAvailable,
                        });
                    }
                    case "qbittorrent":
                    {
                        if (body.ClientId is not int clientId) return Results.BadRequest(new { error = "clientId is required" });
                        var client = await c.DownloadClients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clientId, ct);
                        if (client is null) return Results.NotFound();
                        if (!client.Enabled) return Results.Ok(new { ok = false, service, detail = $"Download client '{client.Name}' is disabled." });
                        var movies = await downloadClient.ListAsync(client, client.MovieCategory, ct);
                        var tv = await downloadClient.ListAsync(client, client.TvCategory, ct);
                        var all = movies.Concat(tv).ToList();
                        return Results.Ok(new
                        {
                            ok = true, service, name = client.Name,
                            detail = $"qBittorrent '{client.Name}' signed in and listed {movies.Count} torrent(s) in '{client.MovieCategory}' and {tv.Count} in '{client.TvCategory}'.",
                            movieCategory = client.MovieCategory, movieCount = movies.Count,
                            tvCategory = client.TvCategory, tvCount = tv.Count,
                            states = all.GroupBy(t => t.State).Select(g => new { state = g.Key, count = g.Count() }),
                            anyContentPath = all.Any(t => !string.IsNullOrEmpty(t.ContentPath)),
                        });
                    }
                    case "legacy":
                    {
                        if (string.IsNullOrWhiteSpace(body.Kind) || string.IsNullOrWhiteSpace(body.BaseUrl) || string.IsNullOrWhiteSpace(body.ApiKey))
                            return Results.BadRequest(new { error = "kind, baseUrl and apiKey are required for a legacy test" });
                        var items = await legacyReader.ReadAsync(new LegacyArrSource(body.Kind, body.BaseUrl, body.ApiKey), ct);
                        var readable = items.Where(i => i.Error is null).ToList();
                        return Results.Ok(new
                        {
                            ok = true, service, name = body.Kind,
                            detail = $"{body.Kind} answered with {readable.Count} readable catalog item(s) and {items.Count - readable.Count} item error(s). No item was copied or modified.",
                            readableCount = readable.Count, errorCount = items.Count - readable.Count,
                            sampleTitles = readable.Take(3).Select(i => i.Title),
                        });
                    }
                    default:
                        return Results.BadRequest(new { error = "Unknown service; use tmdb, torznab, qbittorrent or legacy" });
                }
            }
            catch (Exception ex) when (ex is ExternalServiceException or ProviderNotConfiguredException or ArgumentException or InvalidOperationException)
            {
                return Results.Ok(new { ok = false, service, detail = OutboundHttp.Redact(ex.Message) });
            }
        });

        api.MapPost("/migration/preview", async (MigrationPreviewBody body, ILegacyArrReader reader, CancellationToken ct) =>
        {
            var items = await reader.ReadAsync(new LegacyArrSource(body.Kind, body.BaseUrl, body.ApiKey), ct);
            return Results.Ok(new
            {
                readOnly = true,
                items = items.Where(i => i.Error is null).Select(i => new { type = i.Type == MediaType.Movie ? "movie" : "series", i.Title, i.Year, i.TmdbId, i.TvdbId, i.ImdbId, i.Path, i.Monitored, seasons = (i.Seasons ?? []).Select(s => new { s.SeasonNumber, s.Monitored }), episodes = (i.Episodes ?? []).Select(e => new { e.SeasonNumber, e.EpisodeNumber, e.Monitored }), i.EpisodesUnknown }),
                errors = items.Where(i => i.Error is not null).Select(i => new { i.Title, i.Error }),
            });
        });

        api.MapPost("/migration/import", async (MigrationImportBody body, MediaService media, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            var results = new List<object>();
            foreach (var sel in body.Items.Take(500))
            {
                if (sel.TmdbId is not int tmdb)
                {
                    results.Add(new { sel.Title, status = "error", message = "No TMDb id; skipped" });
                    continue;
                }
                try
                {
                    var type = ParseType(sel.Type);
                    // Safe skip, not a silent default: a new series with no season monitoring data must
                    // never be imported as "all monitored" — that was the original inflated-wanted bug.
                    // An already-imported series is different: AddAsync returns it unchanged, so it still
                    // reports "exists" and only its legacy path is filled in below.
                    if (type == MediaType.Series && sel.Seasons is not { Count: > 0 })
                    {
                        await using var check = await db.CreateDbContextAsync(ct);
                        var exists = await check.MediaItems.AnyAsync(m => m.MediaType == type && m.Provider == "tmdb" && m.ProviderId == tmdb.ToString(), ct);
                        if (ShouldSkipNewSeriesImport(type, sel.Seasons, exists))
                        {
                            results.Add(new { sel.Title, status = "skipped", message = $"No season monitoring data from {sel.LegacySource ?? "the legacy source"}; skipped so no season is silently marked monitored. Import again once seasons are reported, or check the series in Sonarr." });
                            continue;
                        }
                    }
                    // New series keep Sonarr's season flags and, when supplied, per-episode flags.
                    // Existing items are returned unchanged here; use /migration/monitoring-sync for them.
                    var monitorSeasons = sel.Seasons is { Count: > 0 } ? sel.Seasons.Where(s => s.Monitored).Select(s => s.SeasonNumber).ToList() : null;
                    var monitorEpisodes = sel.Episodes is { Count: > 0 } ? sel.Episodes.Select(e => new EpisodeMonitor(e.SeasonNumber, e.EpisodeNumber, e.Monitored)).ToList() : null;
                    var r = await media.AddAsync(new AddMediaRequest(type, tmdb.ToString(), body.QualityProfileId, body.RootFolder, sel.Monitored, monitorSeasons, monitorSeasons is null, monitorEpisodes), ct);
                    // Keep the original path so existing files can be matched. An existing value is never overwritten.
                    if (sel.LegacyPath is { Length: > 0 } legacy)
                    {
                        await using var c = await db.CreateDbContextAsync(ct);
                        var item = await c.MediaItems.FindAsync([r.Item.Id], ct);
                        if (item is { LegacyPath: null })
                        {
                            item.LegacyPath = legacy.Trim();
                            item.LegacySource = sel.LegacySource;
                            await c.SaveChangesAsync(ct);
                        }
                    }
                    var warning = r.Created && sel.EpisodesUnknown
                        ? "Warning: episode monitoring data was unavailable, so episodes took their season flags. Run monitoring sync later to restore per-episode exceptions."
                        : null;
                    results.Add(new { sel.Title, status = r.Created ? "added" : "exists", id = r.Item.Id, message = warning });
                }
                catch (Exception ex) when (ex is ExternalServiceException or ProviderNotConfiguredException or ArgumentException)
                {
                    results.Add(new { sel.Title, status = "error", message = OutboundHttp.Redact(ex.Message) });
                }
            }
            return Results.Ok(new { results });
        });

        // Monitoring sync for already-imported items. Preview by default (apply: false): no writes at all.
        // Apply only changes Monitored flags on matched items; it never creates items, never touches file
        // state, quality profiles, root folders, or titles, and a body-supplied key is never stored.
        // Apply requires the exact preview the user saw: the server recomputes from the current source
        // and the current database, compares it to the submitted preview, and refuses with 409 (plus the
        // fresh preview) if the source, the database, or any proposed change has moved since preview.
        api.MapPost("/migration/monitoring-sync", async (MonitoringSyncBody body, MediaService media, ILegacyArrReader reader, CancellationToken ct) =>
        {
            var items = await reader.ReadAsync(new LegacyArrSource(body.Kind, body.BaseUrl, body.ApiKey), ct);
            var fresh = await media.SyncLegacyMonitoringAsync(items.Where(i => i.Error is null).ToList(), apply: false, ct);
            if (body.Apply == true)
            {
                if (body.Preview is null)
                    return Results.BadRequest(new { error = "Preview required: run Preview monitoring sync for this source first, then Apply the preview you saw." });
                if (!PreviewMatches(fresh, body.Preview))
                    return Results.Conflict(new
                    {
                        error = "The source or proposed changes changed since your preview. Review the fresh preview below before applying.",
                        preview = MapSyncResult(fresh),
                    });
                var applied = await media.SyncLegacyMonitoringAsync(items.Where(i => i.Error is null).ToList(), apply: true, ct);
                return Results.Ok(MapSyncResult(applied, applied: true));
            }
            return Results.Ok(MapSyncResult(fresh));
        });
    }

    /// <summary>Public for testing: the skip rule for series imports with no season monitoring data.</summary>
    public static bool ShouldSkipNewSeriesImport(MediaType type, IReadOnlyList<SeasonSelection>? seasons, bool exists) =>
        type == MediaType.Series && seasons is not { Count: > 0 } && !exists;

    public static object MapSyncResult(MonitoringSyncResult result, bool applied = false) => new
    {
        applied,
        result.ItemsChecked, result.SkippedNoTmdb,
        changes = result.Changes.Select(c => new { c.TmdbId, c.Title, c.Type, c.SeriesMonitoredBefore, c.SeriesMonitoredAfter, c.MonitoredChanged, c.SeasonsChanged, c.EpisodesChanged, c.EpisodesUnknown }),
        unmatchedTitles = result.UnmatchedTitles,
        episodesUnknownTitles = result.EpisodesUnknownTitles,
    };

    /// <summary>Public for testing: exact preview equality, so Apply cannot run on a stale preview.</summary>
    public static bool PreviewMatches(MonitoringSyncResult fresh, MonitoringSyncPreview preview)
    {
        if (fresh.ItemsChecked != preview.ItemsChecked || fresh.SkippedNoTmdb != preview.SkippedNoTmdb) return false;
        if (!fresh.UnmatchedTitles.OrderBy(x => x).SequenceEqual(preview.UnmatchedTitles.OrderBy(x => x))) return false;
        if (!fresh.EpisodesUnknownTitles.OrderBy(x => x).SequenceEqual(preview.EpisodesUnknownTitles.OrderBy(x => x))) return false;
        var freshChanges = fresh.Changes.ToDictionary(c => (c.TmdbId, c.Type));
        var previewChanges = preview.Changes.ToDictionary(c => (c.TmdbId, c.Type));
        if (freshChanges.Count != previewChanges.Count) return false;
        foreach (var (key, f) in freshChanges)
        {
            if (!previewChanges.TryGetValue(key, out var p)) return false;
            if (f.Title != p.Title || f.SeriesMonitoredBefore != p.SeriesMonitoredBefore || f.SeriesMonitoredAfter != p.SeriesMonitoredAfter
                || f.MonitoredChanged != p.MonitoredChanged || f.SeasonsChanged != p.SeasonsChanged || f.EpisodesChanged != p.EpisodesChanged
                || f.EpisodesUnknown != p.EpisodesUnknown) return false;
        }
        return true;
    }

    /// <summary>The folder above a stored legacy path. Radarr and Sonarr store the movie or series folder itself.</summary>
    private static string ParentFolder(string legacyPath)
    {
        var trimmed = legacyPath.TrimEnd('/');
        var cut = trimmed.LastIndexOf('/');
        return cut <= 0 ? "" : trimmed[..cut];
    }

    private static MediaType ParseType(string type) => type.ToLowerInvariant() switch
    {
        "movie" => MediaType.Movie,
        "series" or "tv" => MediaType.Series,
        _ => throw new ArgumentException("Type must be 'movie' or 'series'")
    };

    private static string RequireAbsolute(string path)
    {
        if (!Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("Path must be absolute and must not contain '..'");
        return path.Trim();
    }
}
