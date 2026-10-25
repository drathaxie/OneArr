using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure;
using OneArr.Infrastructure.Files;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Library;
using OneArr.Infrastructure.Legacy;
using OneArr.Infrastructure.Persistence;
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
    public sealed record MigrationSelection(string Type, int? TmdbId, string Title, bool Monitored, string? LegacyPath = null, string? LegacySource = null);
    public sealed record LibraryRootBody(string Type, string Path, string? Label);
    public sealed record PathMappingBody(string Type, string SourcePrefix, string TargetPrefix);
    public sealed record ApplyBody(List<string>? ExcludePaths);

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

        api.MapPatch("/media/{id:int}", async (int id, PatchMediaBody body, MediaService media, CancellationToken ct) =>
            await media.UpdateAsync(id, body.Monitored, body.QualityProfileId, body.RootFolder, ct) ? Results.NoContent() : Results.NotFound());

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

        // Read-only preview. It stores the reviewed result and changes no media rows or files.
        api.MapPost("/library/preview", async (LibraryReconciler library, CancellationToken ct) => Results.Ok(await library.PreviewAsync(ct)));

        api.MapPost("/library/plans/{id:int}/apply", async (int id, ApplyBody? body, LibraryReconciler library, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await library.ApplyAsync(id, body?.ExcludePaths, ct));
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

        api.MapPost("/migration/preview", async (MigrationPreviewBody body, ILegacyArrReader reader, CancellationToken ct) =>
        {
            var items = await reader.ReadAsync(new LegacyArrSource(body.Kind, body.BaseUrl, body.ApiKey), ct);
            return Results.Ok(new
            {
                readOnly = true,
                items = items.Where(i => i.Error is null).Select(i => new { type = i.Type == MediaType.Movie ? "movie" : "series", i.Title, i.Year, i.TmdbId, i.TvdbId, i.ImdbId, i.Path, i.Monitored }),
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
                    var r = await media.AddAsync(new AddMediaRequest(ParseType(sel.Type), tmdb.ToString(), body.QualityProfileId, body.RootFolder, sel.Monitored, null, true), ct);
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
                    results.Add(new { sel.Title, status = r.Created ? "added" : "exists", id = r.Item.Id });
                }
                catch (Exception ex) when (ex is ExternalServiceException or ProviderNotConfiguredException or ArgumentException)
                {
                    results.Add(new { sel.Title, status = "error", message = OutboundHttp.Redact(ex.Message) });
                }
            }
            return Results.Ok(new { results });
        });
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
