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
    public sealed record GrabBody(int MediaItemId, int? EpisodeId, string Title, string Link, string? InfoHash, int? DownloadClientId = null);
    // ApiKey/Password are null-or-empty on PUT to mean "keep the stored secret"; a non-empty value replaces it.
    // Priority: LOWER values are searched first; ties break by Id. Default 0 preserves Id order.
    public sealed record IndexerBody(string Name, string BaseUrl, string? ApiKey, bool Enabled, bool MoviesEnabled = true, bool TvEnabled = true, int Priority = 0,
        string? TrackerId = null, Dictionary<string, string>? TrackerSettings = null, string? Protocol = null);
    public sealed record ClientBody(string Name, string BaseUrl, string Username, string? Password, string? MovieCategory, string? TvCategory, bool Enabled);
    public sealed record DownloadPathPreviewBody(string RemotePrefix, string LocalPrefix, string ReportedPath);
    public sealed record SettingsBody(string? TmdbApiKey, bool? DryRunImport, string? TorrentRoot, string? MediaRoot, bool? AutomaticGrabbing = null, string? DefaultMovieRoot = null, string? DefaultSeriesRoot = null, int? DefaultMovieProfileId = null, int? DefaultSeriesProfileId = null);
    public sealed record ProfileBody(string Name, int MinResolution, int CutoffResolution, bool AllowWebSources);
    public sealed record NamingPreviewBody(string Kind, string Template, string Title, string OriginalFile, int? Year = null, int? Season = null, int? Episode = null, string? EpisodeTitle = null, string? Quality = null, string? ReleaseGroup = null);
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
    // For a saved record pass IndexerId/ClientId. For unsaved form values pass BaseUrl (+ ApiKey for
    // torznab, + Username/Password/categories for qbittorrent) with no Id: the values are tested
    // without being saved, and no health row is written because there is nothing to attach it to.
    public sealed record ConnectionTestBody(string Service, int? IndexerId = null, int? ClientId = null, string? Kind = null, string? BaseUrl = null, string? ApiKey = null, string? Username = null, string? Password = null, string? MovieCategory = null, string? TvCategory = null,
        string? TrackerId = null, Dictionary<string, string>? TrackerSettings = null, string? Protocol = null);
    public sealed record ApplyPreviewBody(int PreviewId, bool Confirmed);
    public sealed record BulkImportBody(bool Confirmed, List<BulkApproval>? Approvals);

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
            var validation = ValidateProfileBody(body);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            await using var c = await db.CreateDbContextAsync(ct);
            var profile = new QualityProfile
            {
                Name = body.Name.Trim(),
                MinResolution = body.MinResolution,
                CutoffResolution = body.CutoffResolution,
                AllowWebSources = body.AllowWebSources
            };
            c.QualityProfiles.Add(profile);
            await c.SaveChangesAsync(ct);
            return Results.Created($"/api/quality-profiles/{profile.Id}", profile);
        });

        api.MapPut("/quality-profiles/{id:int}", async (int id, ProfileBody body,
            IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            var validation = ValidateProfileBody(body);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            await using var c = await db.CreateDbContextAsync(ct);
            var profile = await c.QualityProfiles.FindAsync([id], ct);
            if (profile is null) return Results.NotFound();
            profile.Name = body.Name.Trim();
            profile.MinResolution = body.MinResolution;
            profile.CutoffResolution = body.CutoffResolution;
            profile.AllowWebSources = body.AllowWebSources;
            await c.SaveChangesAsync(ct);
            return Results.Ok(profile);
        });

        api.MapDelete("/quality-profiles/{id:int}", async (int id,
            IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            await using var tx = await c.Database.BeginTransactionAsync(ct);
            var profile = await c.QualityProfiles.FindAsync([id], ct);
            if (profile is null) return Results.NotFound();

            if (await c.QualityProfiles.CountAsync(ct) <= 1)
                return Results.Conflict(new { error = "Cannot delete the last quality profile." });
            if (await c.MediaItems.AnyAsync(m => m.QualityProfileId == id, ct))
                return Results.Conflict(new { error = "Profile is assigned to existing movies or series. Reassign them first." });
            var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (await c.Settings.AnyAsync(s => (s.Key == SettingKeys.DefaultMovieProfile ||
                s.Key == SettingKeys.DefaultSeriesProfile) && s.Value == key, ct))
                return Results.Conflict(new { error = "Profile is a default for new movies or series. Select a different default first." });

            c.QualityProfiles.Remove(profile);
            await c.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // Pure preview: never stores naming preferences and never touches the filesystem.
        // Existing imports still preserve original filenames until a later,
        // separately tested opt-in naming policy is implemented.
        api.MapPost("/naming/preview", (NamingPreviewBody body) =>
        {
            try
            {
                var file = MediaNaming.Preview(new MediaNaming.Request(
                    body.Kind, body.Template, body.Title, body.OriginalFile, body.Year,
                    body.Season, body.Episode, body.EpisodeTitle, body.Quality, body.ReleaseGroup));
                return Results.Ok(new { fileName = file, applied = false });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/metadata/search", async (string q, string type, IMetadataProvider metadata, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length > 200) return Results.BadRequest(new { error = "Query must be 1-200 characters" });
            var mt = ParseType(type);
            return Results.Ok(await metadata.SearchAsync(mt, q.Trim(), ct));
        });

        // List view: series carry episode-based availability (Complete/Partial/Missing/Unknown plus counts)
        // from one aggregate query, not from the series-level HasFile flag, which is true as soon as any
        // single episode has a file. Movies keep Available/Missing from HasFile.
        api.MapGet("/media", async (string type, MediaService media, CancellationToken ct) =>
        {
            var mt = ParseType(type);
            var items = await media.ListAsync(mt, ct);
            var availability = mt == MediaType.Series ? await media.SeriesAvailabilityAsync(ct) : new Dictionary<int, SeriesAvailability>();
            return Results.Ok(items.Select(m => new
            {
                m.Id, m.MediaType, m.ProviderId, m.Title, m.Year, m.Overview, m.PosterPath, m.Monitored,
                m.QualityProfileId, m.RootFolder, m.HasFile, m.LegacyPath, m.LegacySource,
                availability = mt == MediaType.Movie
                    ? (m.HasFile ? "Available" : "Missing")
                    : availability.TryGetValue(m.Id, out var a) ? a.Status : "Unknown",
                availableEpisodes = mt == MediaType.Series ? availability.TryGetValue(m.Id, out var av) ? av.AvailableEpisodes : 0 : (int?)null,
                applicableEpisodes = mt == MediaType.Series ? availability.TryGetValue(m.Id, out var ap) ? ap.ApplicableEpisodes : 0 : (int?)null,
            }));
        });

        api.MapPost("/media", async (AddMediaBody body, MediaService media, SettingsStore settings, CancellationToken ct) =>
        {
            // An explicit root folder wins. Otherwise the per-type default wins — and nothing else.
            // There is deliberately no fallback to the legacy shared media root: when that root was a
            // Movies folder, series adds silently landed in it. With no per-type default, the request
            // fails with a clear message instead of guessing a folder. Re-adding an existing title never
            // overwrites its stored root folder, profile, or monitoring (AddAsync returns it unchanged).
            var root = body.RootFolder;
            if (string.IsNullOrWhiteSpace(root))
            {
                var movieRoot = await settings.GetAsync(SettingKeys.DefaultMovieRoot, ct);
                var seriesRoot = await settings.GetAsync(SettingKeys.DefaultSeriesRoot, ct);
                root = MediaDefaults.ResolveRoot(ParseType(body.Type), movieRoot, seriesRoot);
                if (string.IsNullOrWhiteSpace(root))
                    throw new ArgumentException($"No default root folder is set for {body.Type.ToLowerInvariant()} titles. Set one in Settings → Media Management → Default root folders, or provide a root folder with this request.");
            }
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
            // Same rule as the list view: monitored, aired episodes only. The old detail status counted
            // every episode, so a series with one downloaded episode and many unmonitored or unaired
            // episodes could still read as Partial, and a series with any file read as Available.
            var applicableEpisodes = item.Episodes.Where(e => e.Monitored && e.AirDate != null && e.AirDate <= today).ToList();
            var availableEpisodes = applicableEpisodes.Count(e => e.HasFile);
            var seriesAvailability = item.MediaType == MediaType.Series
                ? MediaDefaults.ClassifySeries(availableEpisodes, applicableEpisodes.Count)
                : item.HasFile ? "Available" : "Missing";
            return Results.Ok(new
            {
                item.Id, item.MediaType, item.Title, item.Year, item.Overview, item.PosterPath, item.Monitored,
                item.QualityProfileId, item.RootFolder, item.HasFile, item.LegacyPath, item.LegacySource,
                availability = seriesAvailability,
                availableEpisodes = item.MediaType == MediaType.Series ? availableEpisodes : (int?)null,
                applicableEpisodes = item.MediaType == MediaType.Series ? applicableEpisodes.Count : (int?)null,
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
                // Per-indexer transparency: which indexers returned results, returned none, failed,
                // or were skipped (disabled / media type not enabled). A failed indexer never hides
                // behind another's results, and a successful connection test is not a search.
                indexers = result.IndexerOutcomes.Select(o => new { o.Id, o.Name, status = o.Status, resultCount = o.ResultCount, message = o.Message }),
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
                    publishDateUtc = d.Candidate.PublishDateUtc,
                }),
            });
        });

        api.MapPost("/grab", async (GrabBody body, ReleaseService releases, CancellationToken ct) =>
        {
            var job = await releases.GrabAsync(new GrabRequest(body.MediaItemId, body.EpisodeId, body.Title, body.Link, body.InfoHash, body.DownloadClientId), ct);
            return Results.Created($"/api/queue/{job.Id}", job);
        });

        // Indexer responses NEVER include the API key — only whether one is stored. Edits keep the
        // stored key when the body leaves it empty, so the UI never needs to round-trip a secret.
        api.MapGet("/indexers", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var indexers = await c.Indexers.AsNoTracking().OrderBy(i => i.Priority).ThenBy(i => i.Id).ToListAsync(ct);
            var health = await c.IndexerHealth.AsNoTracking().ToDictionaryAsync(h => h.IndexerId, ct);
            return Results.Ok(indexers.Select(i => IndexerDto(i, health.GetValueOrDefault(i.Id))).ToList());
        });

        api.MapPost("/indexers", async (IndexerBody body, IDbContextFactory<OneArrDbContext> db, TrackerSecretBox secrets, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name)) throw new ArgumentException("Indexer name is required");
            UrlGuard.RequireServiceUrl(body.BaseUrl);
            await using var c = await db.CreateDbContextAsync(ct);
            if (body.Protocol is not null && body.Protocol is not ("rss" or "torznab"))
                throw new ArgumentException("Unsupported indexer protocol");
            if (body.Protocol == "rss" && body.TrackerId is not null)
                throw new ArgumentException("RSS cannot use a native account-login adapter");
            var secret = body.Protocol == "rss" ? PublicRssClient.ProtocolMarker :
                body.TrackerId is not null ? CreateNativeSecret(body.TrackerId, body.TrackerSettings, secrets, null, body.BaseUrl)
                : body.ApiKey ?? "";
            var row = new IndexerConfig { Name = body.Name.Trim(), BaseUrl = body.BaseUrl.Trim(), ApiKey = secret, Enabled = body.Enabled, MoviesEnabled = body.MoviesEnabled, TvEnabled = body.TvEnabled, Priority = body.Priority };
            c.Indexers.Add(row);
            await c.SaveChangesAsync(ct);
            return Results.Created($"/api/indexers/{row.Id}", IndexerDto(row, null));
        });

        api.MapPut("/indexers/{id:int}", async (int id, IndexerBody body, IDbContextFactory<OneArrDbContext> db, TrackerSecretBox secrets, NativeTrackerClient native, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name)) throw new ArgumentException("Indexer name is required");
            UrlGuard.RequireServiceUrl(body.BaseUrl);
            await using var c = await db.CreateDbContextAsync(ct);
            var row = await c.Indexers.FindAsync([id], ct);
            if (row is null) return Results.NotFound();
            row.Name = body.Name.Trim();
            row.BaseUrl = body.BaseUrl.Trim();
            if (body.Protocol is not null && body.Protocol is not ("rss" or "torznab"))
                throw new ArgumentException("Unsupported indexer protocol");
            if (body.Protocol == "rss" && (body.TrackerId is not null || TrackerSecretBox.IsNative(row.ApiKey)))
                throw new ArgumentException("Cannot convert native account tracker into RSS");
            // Explicitly selected RSS never carries an API key or password.
            if (body.Protocol == "rss")
                row.ApiKey = PublicRssClient.ProtocolMarker;
            else if (PublicRssClient.IsPublicRss(row.ApiKey))
            {
                if (body.Protocol == "torznab")
                    row.ApiKey = body.ApiKey ?? "";
                // A normal edit on an RSS indexer retains RSS protocol.
            }
            // Native credentials remain encrypted; blank edit fields preserve their existing secrets.
            else if (body.TrackerId is not null)
                row.ApiKey = CreateNativeSecret(body.TrackerId, body.TrackerSettings, secrets, row.ApiKey, body.BaseUrl);
            else if (TrackerSecretBox.IsNative(row.ApiKey))
            {
                // A native indexer may be toggled/renamed, but may not silently
                // become a plain Torznab indexer or move to an unsafe host.
                _ = NativeTrackerClient.ValidateSiteUrl(body.BaseUrl);
            }
            else if (!string.IsNullOrEmpty(body.ApiKey)) row.ApiKey = body.ApiKey;
            native.Invalidate(row.Id);
            row.Enabled = body.Enabled;
            row.MoviesEnabled = body.MoviesEnabled;
            row.TvEnabled = body.TvEnabled;
            row.Priority = body.Priority;
            await c.SaveChangesAsync(ct);
            var h = await c.IndexerHealth.AsNoTracking().FirstOrDefaultAsync(x => x.IndexerId == id, ct);
            return Results.Ok(IndexerDto(row, h));
        });

        api.MapDelete("/indexers/{id:int}", async (int id, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var row = await c.Indexers.FindAsync([id], ct);
            if (row is null) return Results.NotFound();
            c.Indexers.Remove(row);
            var h = await c.IndexerHealth.FindAsync([id], ct);
            if (h is not null) c.IndexerHealth.Remove(h);
            await c.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Client responses NEVER include the password — only whether one is stored. Edits keep the
        // stored password when the body leaves it empty. Deleting a client never touches download
        // jobs: they keep their owning-client id and queue sync never reassigns them.
        api.MapGet("/download-clients", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var clients = await c.DownloadClients.AsNoTracking().OrderBy(d => d.Id).ToListAsync(ct);
            var health = await c.DownloadClientHealth.AsNoTracking().ToDictionaryAsync(h => h.DownloadClientId, ct);
            return Results.Ok(clients.Select(d => ClientDto(d, health.GetValueOrDefault(d.Id))).ToList());
        });

        api.MapPost("/download-clients", async (ClientBody body, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name)) throw new ArgumentException("Client name is required");
            UrlGuard.RequireServiceUrl(body.BaseUrl);
            await using var c = await db.CreateDbContextAsync(ct);
            var row = new DownloadClientConfig
            {
                Name = body.Name.Trim(),
                BaseUrl = body.BaseUrl.Trim(),
                Username = body.Username,
                Password = body.Password ?? "",
                MovieCategory = string.IsNullOrWhiteSpace(body.MovieCategory) ? "onearr-movies" : body.MovieCategory.Trim(),
                TvCategory = string.IsNullOrWhiteSpace(body.TvCategory) ? "onearr-tv" : body.TvCategory.Trim(),
                Enabled = body.Enabled,
            };
            c.DownloadClients.Add(row);
            await c.SaveChangesAsync(ct);
            return Results.Created($"/api/download-clients/{row.Id}", ClientDto(row, null));
        });

        api.MapPut("/download-clients/{id:int}", async (int id, ClientBody body, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name)) throw new ArgumentException("Client name is required");
            UrlGuard.RequireServiceUrl(body.BaseUrl);
            await using var c = await db.CreateDbContextAsync(ct);
            var row = await c.DownloadClients.FindAsync([id], ct);
            if (row is null) return Results.NotFound();
            row.Name = body.Name.Trim();
            row.BaseUrl = body.BaseUrl.Trim();
            row.Username = body.Username;
            // An empty password on edit preserves the stored password; a non-empty one replaces it.
            if (!string.IsNullOrEmpty(body.Password)) row.Password = body.Password;
            row.MovieCategory = string.IsNullOrWhiteSpace(body.MovieCategory) ? "onearr-movies" : body.MovieCategory.Trim();
            row.TvCategory = string.IsNullOrWhiteSpace(body.TvCategory) ? "onearr-tv" : body.TvCategory.Trim();
            row.Enabled = body.Enabled;
            await c.SaveChangesAsync(ct);
            var h = await c.DownloadClientHealth.AsNoTracking().FirstOrDefaultAsync(x => x.DownloadClientId == id, ct);
            return Results.Ok(ClientDto(row, h));
        });

        api.MapDelete("/download-clients/{id:int}", async (int id, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var row = await c.DownloadClients.FindAsync([id], ct);
            if (row is null) return Results.NotFound();
            c.DownloadClients.Remove(row);
            var h = await c.DownloadClientHealth.FindAsync([id], ct);
            if (h is not null) c.DownloadClientHealth.Remove(h);
            await c.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Download-client remote path mappings are independent of legacy media-library
        // migration mappings. One source/destination prefix per client for now.
        api.MapGet("/download-clients/{id:int}/path-mapping", async (int id,
            IDbContextFactory<OneArrDbContext> db, SettingsStore settings, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            if (!await c.DownloadClients.AnyAsync(d => d.Id == id, ct)) return Results.NotFound();
            var json = await settings.GetAsync(DownloadPathMapper.SettingsKey(id), ct);
            return Results.Ok(new { mapping = json is null ? null : DownloadPathMapper.Read(json) });
        });

        api.MapPut("/download-clients/{id:int}/path-mapping", async (int id,
            DownloadPathMapping rule, IDbContextFactory<OneArrDbContext> db,
            SettingsStore settings, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            if (!await c.DownloadClients.AnyAsync(d => d.Id == id, ct)) return Results.NotFound();
            var torrentRoot = await settings.GetAsync(SettingKeys.TorrentRoot, ct) ?? "";
            DownloadPathMapper.Validate(rule, torrentRoot);
            await settings.SetAsync(DownloadPathMapper.SettingsKey(id), DownloadPathMapper.Serialize(rule), ct);
            return Results.Ok(rule);
        });

        api.MapPost("/download-clients/{id:int}/path-mapping/preview", async (int id,
            DownloadPathPreviewBody body, IDbContextFactory<OneArrDbContext> db,
            SettingsStore settings, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            if (!await c.DownloadClients.AnyAsync(d => d.Id == id, ct)) return Results.NotFound();
            var torrentRoot = await settings.GetAsync(SettingKeys.TorrentRoot, ct) ?? "";
            var local = DownloadPathMapper.Map(new DownloadPathMapping(
                body.RemotePrefix, body.LocalPrefix), body.ReportedPath, torrentRoot);
            return Results.Ok(new { reportedPath = body.ReportedPath, localPath = local,
                exists = File.Exists(local) || Directory.Exists(local), applied = false });
        });

        api.MapDelete("/download-clients/{id:int}/path-mapping", async (int id,
            IDbContextFactory<OneArrDbContext> db, SettingsStore settings, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            if (!await c.DownloadClients.AnyAsync(d => d.Id == id, ct)) return Results.NotFound();
            await settings.SetAsync(DownloadPathMapper.SettingsKey(id), null, ct);
            return Results.NoContent();
        });

        // Queue with client health. Raw grab Links are deliberately NOT returned: they can embed
        // indexer API keys, and Activity never needs them.
        api.MapGet("/queue", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var jobs = await c.DownloadJobs.AsNoTracking().OrderByDescending(j => j.Id).Take(200).ToListAsync(ct);
            var health = await c.DownloadClientHealth.AsNoTracking().ToListAsync(ct);
            var clients = await c.DownloadClients.AsNoTracking().ToListAsync(ct);
            return Results.Ok(new
            {
                jobs = jobs.Select(j => new
                {
                    j.Id, j.MediaItemId, j.EpisodeId, j.MediaType, j.ReleaseTitle, j.Hash, j.Category,
                    j.DownloadClientId, j.ClientName, j.Status, j.Progress, j.ClientState,
                    j.DownloadSpeedBps, j.EtaSeconds, j.ContentPath, j.Error, j.ImportFailed,
                    j.LastSeenAt, j.CompletedAt, j.CreatedAt, j.UpdatedAt,
                    importReady = j.Status == DownloadStatus.Completed && j.ContentPath != null,
                }),
                clients = clients.Select(cl => new
                {
                    cl.Id, cl.Name, cl.Enabled,
                    lastSeenAt = health.FirstOrDefault(h => h.DownloadClientId == cl.Id)?.LastSeenAt,
                    lastError = health.FirstOrDefault(h => h.DownloadClientId == cl.Id)?.LastError,
                }),
            });
        });

        // Import preview: plans without touching disk beyond reads. Apply requires the preview id
        // plus explicit confirmation, runs under the same "import" lock as bulk import (so a
        // scheduled import and a preview apply can never compete over one download), and refuses
        // stale plans with 409 + a fresh preview.
        api.MapPost("/queue/{id:int}/import-preview", async (int id, DownloadService downloads, CancellationToken ct) =>
        {
            try { return Results.Ok(await downloads.PreviewImportAsync(id, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/queue/{id:int}/import-apply", async (int id, ApplyPreviewBody body, DownloadService downloads, JobRunner runner, CancellationToken ct) =>
        {
            if (body is null || !body.Confirmed) return Results.BadRequest(new { error = "Import apply requires explicit confirmation" });
            // JobRunner records work exceptions instead of throwing them, so refused applies are
            // captured here explicitly — otherwise a refused apply would look like success.
            ImportPreviewDto? applied = null;
            ImportPreviewDto? fresh = null;
            string? error = null;
            var ran = await runner.TryRunAsync("import", async t =>
            {
                try
                {
                    applied = await downloads.ApplyPreviewAsync(id, body.PreviewId, t);
                    return "Import applied";
                }
                catch (StalePreviewException ex)
                {
                    fresh = ex.Fresh;
                    return "Preview stale";
                }
                catch (ArgumentException ex)
                {
                    error = ex.Message;
                    return "Apply refused";
                }
            }, ct);
            if (!ran) return Results.Conflict(new { error = "Import already running" });
            if (error is not null) return Results.BadRequest(new { error });
            if (fresh is not null) return Results.Conflict(new { error = "The import plan changed since the preview; review the fresh preview before applying.", preview = fresh });
            return Results.Ok(applied);
        });

        // Retry a failed import. Only Failed + ImportFailed jobs qualify, and only when they have
        // a stored preview: the retry applies that preview. A stale preview is a 409 carrying a
        // fresh preview, exactly like a stale apply. It never deletes torrents or files.
        api.MapPost("/queue/{id:int}/retry-import", async (int id, DownloadService downloads, JobRunner runner, CancellationToken ct) =>
        {
            ImportRunSummary? summary = null;
            string? error = null;
            ImportPreviewDto? fresh = null;
            var ran = await runner.TryRunAsync("import", async t =>
            {
                try
                {
                    summary = await downloads.RetryImportAsync(id, t);
                    return summary.Message;
                }
                catch (StalePreviewException ex)
                {
                    fresh = ex.Fresh;
                    return "Preview stale";
                }
                catch (ArgumentException ex)
                {
                    error = ex.Message;
                    return "Retry refused";
                }
            }, ct);
            if (!ran) return Results.Conflict(new { error = "Import already running" });
            if (error is not null) return Results.BadRequest(new { error });
            if (fresh is not null) return Results.Conflict(new { error = "The import plan changed since the preview; review the fresh preview before applying.", preview = fresh });
            return Results.Ok(new { message = summary?.Message ?? "", imported = summary?.Imported ?? 0, failed = summary?.Failed ?? 0 });
        });

        // Activity history: a readable timeline derived from records OneArr already keeps. No new
        // history table, so events from before this PR are only as precise as the records kept then
        // (CompletedAt is null for jobs completed before client tracking existed).
        api.MapGet("/activity/history", async (IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var jobs = await c.DownloadJobs.AsNoTracking().OrderByDescending(j => j.Id).Take(200).ToListAsync(ct);
            var records = await c.ImportRecords.AsNoTracking().OrderByDescending(r => r.Id).Take(200).ToListAsync(ct);
            var previews = await c.ImportPreviews.AsNoTracking().OrderByDescending(p => p.Id).Take(200).ToListAsync(ct);
            var scans = await c.LibraryScanPlans.AsNoTracking().Where(p => p.AppliedUtc != null).OrderByDescending(p => p.Id).Take(50).ToListAsync(ct);
            var entries = new List<(DateTimeOffset At, string Kind, string Title, int? JobId)>();
            foreach (var j in jobs)
            {
                entries.Add((j.CreatedAt, "grabbed", $"Grabbed {j.ReleaseTitle}", j.Id));
                if (j.CompletedAt is not null) entries.Add((j.CompletedAt.Value, "completed", $"Download completed: {j.ReleaseTitle}", j.Id));
            }
            foreach (var p in previews) entries.Add((p.CreatedAt, "preview", $"Import preview generated (job {p.DownloadJobId})", p.DownloadJobId));
            foreach (var r in records)
            {
                var kind = r.Action == ImportAction.Unmatched ? "import-unmatched" : r.Action == ImportAction.Deferred ? "deferred" : r.Success ? "imported" : "import-failed";
                var title = r.Action == ImportAction.Unmatched ? $"Imported but unmatched: {r.SourcePath}"
                    : r.Action == ImportAction.Deferred ? $"Import deferred: {r.SourcePath}"
                    : r.Success ? $"Imported {r.SourcePath}" : $"Import failed: {r.SourcePath}";
                entries.Add((r.CreatedAt, kind, title, r.DownloadJobId));
            }
            foreach (var s in scans) entries.Add((s.AppliedUtc!.Value, "reconciled", "Library reconciliation completed", null));
            return Results.Ok(entries.OrderByDescending(e => e.At).Take(200).Select(e => new { at = e.At, kind = e.Kind, title = e.Title, jobId = e.JobId }));
        });

        // Activity problems: operational blockers computed from current records. Actions named here
        // are safe by construction: refresh only re-polls, preview never writes, retry only imports.
        api.MapGet("/activity/problems", async (IDbContextFactory<OneArrDbContext> db, SettingsStore settings, CancellationToken ct) =>
        {
            await using var c = await db.CreateDbContextAsync(ct);
            var problems = new List<object>();
            var clients = await c.DownloadClients.AsNoTracking().Where(x => x.Enabled).ToListAsync(ct);
            var health = await c.DownloadClientHealth.AsNoTracking().ToListAsync(ct);
            foreach (var cl in clients)
            {
                var h = health.FirstOrDefault(x => x.DownloadClientId == cl.Id);
                if (h?.LastError is not null)
                    problems.Add(new { kind = "client-unreachable", title = $"Download client unreachable: {cl.Name}", detail = h.LastError, jobId = (int?)null, clientId = (int?)cl.Id, actions = new[] { "refresh" } });
            }
            var jobs = await c.DownloadJobs.AsNoTracking().OrderByDescending(j => j.Id).Take(200).ToListAsync(ct);
            var mediaRoot = await settings.GetAsync(SettingKeys.MediaRoot, ct);
            foreach (var j in jobs)
            {
                switch (j.Status)
                {
                    case DownloadStatus.Missing:
                        problems.Add(new { kind = "download-missing", title = $"Download missing from qBittorrent: {j.ReleaseTitle}", detail = "The torrent is no longer reported by its client. Clear it in Queue to grab again; nothing is deleted.", jobId = (int?)j.Id, clientId = j.DownloadClientId, actions = new[] { "refresh", "clear-missing" } });
                        break;
                    case DownloadStatus.Failed when j.ImportFailed:
                        problems.Add(new { kind = "import-failed", title = $"Import failed: {j.ReleaseTitle}", detail = j.Error ?? "See import records for the per-file reason.", jobId = (int?)j.Id, clientId = j.DownloadClientId, actions = new[] { "preview", "retry" } });
                        break;
                    case DownloadStatus.Failed:
                        problems.Add(new { kind = "download-failed", title = $"Download failed in qBittorrent: {j.ReleaseTitle}", detail = j.Error ?? j.ClientState, jobId = (int?)j.Id, clientId = j.DownloadClientId, actions = new[] { "refresh" } });
                        break;
                }
                if (j.Status == DownloadStatus.Completed && j.MediaItemId is int mid && !string.IsNullOrWhiteSpace(mediaRoot))
                {
                    var item = await c.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid, ct);
                    if (item is not null)
                    {
                        try { SafeImporter.CanonicalUnder(mediaRoot, item.RootFolder); }
                        catch { problems.Add(new { kind = "invalid-destination", title = $"Invalid media destination: {item.Title}", detail = "The title's root folder is outside the configured media root, so its import fails closed.", jobId = (int?)j.Id, clientId = j.DownloadClientId, actions = new[] { "preview" } }); }
                    }
                    if (!string.IsNullOrWhiteSpace(j.ContentPath))
                    {
                        try
                        {
                            var root = Path.GetPathRoot(Path.GetFullPath(j.ContentPath));
                            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                                problems.Add(new { kind = "drive-offline", title = $"Media drive offline for: {j.ReleaseTitle}", detail = $"The volume {root} is not reachable; the download is not marked missing for this.", jobId = (int?)j.Id, clientId = j.DownloadClientId, actions = new[] { "refresh" } });
                        }
                        catch { /* An unparseable recorded path is a sync concern, not a drive problem. */ }
                    }
                }
                if (j.Status == DownloadStatus.Completed && j.Category.Length > 0)
                {
                    var known = clients.Any(cl => cl.MovieCategory == j.Category || cl.TvCategory == j.Category);
                    if (!known)
                        problems.Add(new { kind = "invalid-category", title = $"Unknown download category: {j.ReleaseTitle}", detail = $"Category '{j.Category}' matches no enabled client's movie or TV category.", jobId = (int?)j.Id, clientId = j.DownloadClientId, actions = new[] { "refresh" } });
                }
            }
            // An owned job whose client is disabled or removed stays with that client (sync never
            // re-associates it); surface that here so it is visible instead of silently stalled.
            foreach (var j in jobs)
            {
                if (j.DownloadClientId is int owner && !clients.Any(cl => cl.Id == owner))
                    problems.Add(new { kind = "client-disabled", title = $"Download client disabled or removed: {j.ReleaseTitle}", detail = "This download stays with its original client and is never matched against other clients. Re-enable the client to resume sync.", jobId = (int?)j.Id, clientId = j.DownloadClientId, actions = new[] { "refresh" } });
            }
            var unmatched = await c.ImportRecords.AsNoTracking().Where(r => r.Action == ImportAction.Unmatched).OrderByDescending(r => r.Id).Take(50).ToListAsync(ct);
            foreach (var r in unmatched)
                problems.Add(new { kind = "import-unmatched", title = "Imported file matched no episode", detail = $"{r.SourcePath} → {r.DestinationPath}", jobId = r.DownloadJobId, clientId = (int?)null, actions = new[] { "preview" } });
            var deferred = await c.ImportRecords.AsNoTracking().Where(r => r.Action == ImportAction.Deferred).OrderByDescending(r => r.Id).Take(50).ToListAsync(ct);
            foreach (var r in deferred)
                problems.Add(new { kind = "file-deferred", title = "Import deferred: file still changing", detail = r.SourcePath, jobId = r.DownloadJobId, clientId = (int?)null, actions = new[] { "preview", "retry" } });
            var badVerify = await c.ImportRecords.AsNoTracking().Where(r => !r.Success && r.Message != null && r.Message.Contains("Verification")).OrderByDescending(r => r.Id).Take(50).ToListAsync(ct);
            foreach (var r in badVerify)
                problems.Add(new { kind = "import-verification-failed", title = "Import failed verification", detail = r.SourcePath, jobId = r.DownloadJobId, clientId = (int?)null, actions = new[] { "preview", "retry" } });
            return Results.Ok(problems);
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

        // Bulk import. A dry run only plans (read-only) and reports problems. A real bulk import
        // applies ONLY explicitly confirmed (jobId, previewId) pairs from the request body: a
        // preview that was merely generated never authorizes anything, and a request without
        // confirmation and at least one pair is refused with 400 before anything is applied.
        api.MapPost("/import", async (bool? dryRun, BulkImportBody? body, DownloadService downloads, SettingsStore settings, JobRunner runner, CancellationToken ct) =>
        {
            var dry = dryRun ?? await settings.GetBoolAsync(SettingKeys.DryRunImport, true, ct);
            string? message = null;
            string? error = null;
            var ran = await runner.TryRunAsync("import", async t =>
            {
                try
                {
                    if (dry)
                    {
                        var summary = await downloads.ImportCompletedAsync(true, t);
                        message = summary.Message;
                    }
                    else
                    {
                        var result = await downloads.ApplySelectedAsync(body?.Confirmed ?? false, body?.Approvals ?? [], t);
                        message = result.Message;
                    }
                }
                catch (ArgumentException ex) { error = ex.Message; }
                return message ?? "Import refused";
            }, ct);
            if (!ran) return Results.Conflict(new { error = "Import already running" });
            if (error is not null) return Results.BadRequest(new { error });
            return Results.Ok(new { dryRun = dry, message });
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
                defaultMovieRoot = await settings.GetAsync(SettingKeys.DefaultMovieRoot, ct),
                defaultSeriesRoot = await settings.GetAsync(SettingKeys.DefaultSeriesRoot, ct),
                defaultMovieProfileId = ParseProfileId(await settings.GetAsync(SettingKeys.DefaultMovieProfile, ct)),
                defaultSeriesProfileId = ParseProfileId(await settings.GetAsync(SettingKeys.DefaultSeriesProfile, ct)),
            });
        });

        api.MapPut("/settings", async (SettingsBody body, SettingsStore settings, IDbContextFactory<OneArrDbContext> db, CancellationToken ct) =>
        {
            // Validate the ENTIRE request before writing anything. The old sequential saves could
            // leave earlier fields changed when a later root or profile failed validation, even though
            // the request returned an error — including the automatic-grabbing toggle, which must never
            // flip on the back of a failed save. Only after every check passes does the single
            // SetManyAsync call commit the changes together.
            // Per-type defaults are stored independently, so saving the movie default can never change
            // the series default. An empty string clears a root default (adds for that type then require
            // an explicit folder); a null field means "not provided" and leaves the stored value alone.
            var values = new Dictionary<string, string?>();
            if (body.TmdbApiKey is not null) values[SettingKeys.TmdbApiKey] = string.IsNullOrWhiteSpace(body.TmdbApiKey) ? null : body.TmdbApiKey.Trim();
            if (body.DryRunImport is bool dry) values[SettingKeys.DryRunImport] = dry.ToString();
            if (body.TorrentRoot is not null) values[SettingKeys.TorrentRoot] = RequireAbsolute(body.TorrentRoot);
            if (body.MediaRoot is not null) values[SettingKeys.MediaRoot] = RequireAbsolute(body.MediaRoot);
            if (body.AutomaticGrabbing is bool auto) values[SettingKeys.AutomaticGrab] = auto.ToString();
            if (body.DefaultMovieRoot is not null) values[SettingKeys.DefaultMovieRoot] = string.IsNullOrWhiteSpace(body.DefaultMovieRoot) ? null : RequireAbsolute(body.DefaultMovieRoot);
            if (body.DefaultSeriesRoot is not null) values[SettingKeys.DefaultSeriesRoot] = string.IsNullOrWhiteSpace(body.DefaultSeriesRoot) ? null : RequireAbsolute(body.DefaultSeriesRoot);
            if (body.DefaultMovieProfileId is int movieProfile) values[SettingKeys.DefaultMovieProfile] = (await RequireProfileAsync(db, movieProfile, ct)).ToString();
            if (body.DefaultSeriesProfileId is int seriesProfile) values[SettingKeys.DefaultSeriesProfile] = (await RequireProfileAsync(db, seriesProfile, ct)).ToString();
            await settings.SetManyAsync(values, ct);
            return Results.NoContent();
        });

        // Non-destructive connection tests. Nothing is added, downloaded, imported, or written to a
        // legacy application. Stored keys/passwords are used server-side only and are never returned;
        // a body-supplied legacy key is used for this one test and is never stored.
        api.MapPost("/connections/test", async (ConnectionTestBody body, IDbContextFactory<OneArrDbContext> db,
            IMetadataProvider metadata, IDownloadClient downloadClient,
            ILegacyArrReader legacyReader, IHttpClientFactory httpFactory, NativeTrackerClient native, TrackerSecretBox secrets, CancellationToken ct) =>
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
                        // Saved record with no overrides (row Test): test its stored values
                        // and record the diagnostic outcome. Saved record WITH form overrides
                        // (edit-dialog Test): test the form's URL, falling back to the stored
                        // key when the key field is blank — the dialog never sees the secret —
                        // save nothing, and record no health, since the values tested are not
                        // the saved ones. Unsaved form values (baseUrl, no indexerId): test
                        // exactly what was typed, save nothing, record no health.
                        string name, baseUrl, apiKey;
                        int? savedId = null;
                        if (body.IndexerId is int indexerId)
                        {
                            var indexer = await c.Indexers.AsNoTracking().FirstOrDefaultAsync(i => i.Id == indexerId, ct);
                            if (indexer is null) return Results.NotFound();
                            var hasOverrides = !string.IsNullOrWhiteSpace(body.BaseUrl) ||
                                !string.IsNullOrWhiteSpace(body.ApiKey) || body.Protocol is not null;
                            if (!hasOverrides)
                            {
                                if (!indexer.Enabled) return Results.Ok(new { ok = false, service, detail = $"Indexer '{indexer.Name}' is disabled." });
                                (name, baseUrl, apiKey, savedId) = (indexer.Name, indexer.BaseUrl, indexer.ApiKey, indexer.Id);
                            }
                            else
                            {
                                name = indexer.Name;
                                baseUrl = string.IsNullOrWhiteSpace(body.BaseUrl) ? indexer.BaseUrl : body.BaseUrl;
                                apiKey = body.Protocol == "rss" ? PublicRssClient.ProtocolMarker :
                                    body.Protocol == "torznab" && PublicRssClient.IsPublicRss(indexer.ApiKey) ? body.ApiKey ?? "" :
                                    string.IsNullOrWhiteSpace(body.ApiKey) ? indexer.ApiKey : body.ApiKey;
                            }
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(body.BaseUrl)) return Results.BadRequest(new { error = "indexerId or baseUrl is required" });
                            (name, baseUrl, apiKey) = ("Unsaved indexer", body.BaseUrl,
                                body.Protocol == "rss" ? PublicRssClient.ProtocolMarker : body.ApiKey ?? "");
                        }
                        TorznabCaps caps;
                        string verifiedDetail = "";
                        try
                        {
                            var (ok, detail, parsed) = await TestTorznabAsync(httpFactory, name, baseUrl, apiKey, ct);
                            verifiedDetail = detail;
                            if (!ok)
                            {
                                if (savedId is int failedId) await UpsertIndexerHealthAsync(c, failedId, false, detail, ct);
                                return Results.Ok(new { ok = false, service, detail });
                            }
                            caps = parsed!;
                            if (savedId is int passedId) await UpsertIndexerHealthAsync(c, passedId, true, null, ct);
                        }
                        catch (Exception ex) when (ex is ExternalServiceException or ArgumentException or InvalidOperationException)
                        {
                            if (savedId is int errorId) await UpsertIndexerHealthAsync(c, errorId, false, OutboundHttp.Redact(ex.Message), ct);
                            throw;
                        }
                        return Results.Ok(new
                        {
                            ok = true, service, name,
                            detail = verifiedDetail,
                            categories = caps.Categories.Select(x => new { x.Id, x.Name }),
                            searchAvailable = caps.SearchAvailable, tvSearchAvailable = caps.TvSearchAvailable, movieSearchAvailable = caps.MovieSearchAvailable,
                        });
                    }
                    case "native-tracker":
                    {
                        IndexerConfig testIndexer;
                        int? savedId = null;
                        if (body.IndexerId is int indexerId)
                        {
                            var stored = await c.Indexers.AsNoTracking().FirstOrDefaultAsync(i => i.Id == indexerId, ct);
                            if (stored is null) return Results.NotFound();
                            if (!TrackerSecretBox.IsNative(stored.ApiKey))
                                return Results.BadRequest(new { error = "This indexer is not a native tracker" });
                            var overrides = !string.IsNullOrWhiteSpace(body.BaseUrl) ||
                                (body.TrackerSettings?.Any(v => !string.IsNullOrWhiteSpace(v.Value)) ?? false);
                            if (!overrides)
                            {
                                if (!stored.Enabled) return Results.Ok(new { ok = false, service, detail = "Tracker is disabled." });
                                testIndexer = stored;
                                savedId = stored.Id;
                            }
                            else
                            {
                                var trackerId = secrets.Read(stored.ApiKey).TrackerId;
                                testIndexer = new IndexerConfig { Id = 0, Name = stored.Name,
                                    BaseUrl = string.IsNullOrWhiteSpace(body.BaseUrl) ? stored.BaseUrl : body.BaseUrl,
                                    ApiKey = CreateNativeSecret(trackerId, body.TrackerSettings, secrets, stored.ApiKey,
                                        string.IsNullOrWhiteSpace(body.BaseUrl) ? stored.BaseUrl : body.BaseUrl) };
                            }
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(body.BaseUrl) || body.TrackerId is null)
                                return Results.BadRequest(new { error = "Native tracker ID and URL are required" });
                            testIndexer = new IndexerConfig { Name = "Unsaved native tracker", BaseUrl = body.BaseUrl,
                                ApiKey = CreateNativeSecret(body.TrackerId, body.TrackerSettings, secrets, null, body.BaseUrl) };
                        }
                        try
                        {
                            var detail = await native.TestAsync(testIndexer, ct);
                            if (savedId is int id) await UpsertIndexerHealthAsync(c, id, true, null, ct);
                            return Results.Ok(new { ok = true, service, name = testIndexer.Name, detail });
                        }
                        catch (Exception ex) when (ex is ExternalServiceException or ArgumentException)
                        {
                            if (savedId is int id) await UpsertIndexerHealthAsync(c, id, false, OutboundHttp.Redact(ex.Message), ct);
                            throw;
                        }
                    }
                    case "qbittorrent":
                    {
                        // Saved record with no overrides (row Test): test its stored values
                        // and record the diagnostic outcome. Saved record WITH form overrides
                        // (edit-dialog Test): form values win, blank fields fall back to the
                        // stored record — notably the password, which the dialog never sees —
                        // save nothing, record no health. Unsaved form values (baseUrl, no
                        // clientId): test exactly what was typed, save nothing, record no
                        // health. Empty categories there fall back to the standard defaults,
                        // matching what the form prefills on save.
                        DownloadClientConfig client;
                        int? savedId = null;
                        if (body.ClientId is int clientId)
                        {
                            var saved = await c.DownloadClients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clientId, ct);
                            if (saved is null) return Results.NotFound();
                            var hasOverrides = !string.IsNullOrWhiteSpace(body.BaseUrl) || !string.IsNullOrWhiteSpace(body.Username) || !string.IsNullOrWhiteSpace(body.Password) || !string.IsNullOrWhiteSpace(body.MovieCategory) || !string.IsNullOrWhiteSpace(body.TvCategory);
                            if (!hasOverrides)
                            {
                                if (!saved.Enabled) return Results.Ok(new { ok = false, service, detail = $"Download client '{saved.Name}' is disabled." });
                                client = saved;
                                savedId = saved.Id;
                            }
                            else
                            {
                                client = new DownloadClientConfig
                                {
                                    Name = saved.Name,
                                    BaseUrl = string.IsNullOrWhiteSpace(body.BaseUrl) ? saved.BaseUrl : body.BaseUrl,
                                    Username = string.IsNullOrWhiteSpace(body.Username) ? saved.Username : body.Username,
                                    Password = string.IsNullOrWhiteSpace(body.Password) ? saved.Password : body.Password,
                                    MovieCategory = string.IsNullOrWhiteSpace(body.MovieCategory) ? saved.MovieCategory : body.MovieCategory.Trim(),
                                    TvCategory = string.IsNullOrWhiteSpace(body.TvCategory) ? saved.TvCategory : body.TvCategory.Trim(),
                                };
                            }
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(body.BaseUrl)) return Results.BadRequest(new { error = "clientId or baseUrl is required" });
                            client = new DownloadClientConfig
                            {
                                Name = "Unsaved client",
                                BaseUrl = body.BaseUrl,
                                Username = body.Username ?? "",
                                Password = body.Password ?? "",
                                MovieCategory = string.IsNullOrWhiteSpace(body.MovieCategory) ? "onearr-movies" : body.MovieCategory.Trim(),
                                TvCategory = string.IsNullOrWhiteSpace(body.TvCategory) ? "onearr-tv" : body.TvCategory.Trim(),
                            };
                        }
                        try
                        {
                            var (ok, detail, movies, tv) = await TestQbitAsync(downloadClient, client, ct);
                            if (!ok)
                            {
                                if (savedId is int failedId) await UpsertClientHealthAsync(c, failedId, false, detail, ct);
                                return Results.Ok(new { ok = false, service, detail });
                            }
                            if (savedId is int passedId) await UpsertClientHealthAsync(c, passedId, true, null, ct);
                            var all = movies.Concat(tv).ToList();
                            return Results.Ok(new
                            {
                                ok = true, service, name = client.Name, detail,
                                movieCategory = client.MovieCategory, movieCount = movies.Count,
                                tvCategory = client.TvCategory, tvCount = tv.Count,
                                states = all.GroupBy(t => t.State).Select(g => new { state = g.Key, count = g.Count() }),
                                anyContentPath = all.Any(t => !string.IsNullOrEmpty(t.ContentPath)),
                            });
                        }
                        catch (Exception ex) when (ex is ExternalServiceException or ArgumentException or InvalidOperationException)
                        {
                            if (savedId is int errorId) await UpsertClientHealthAsync(c, errorId, false, OutboundHttp.Redact(ex.Message), ct);
                            throw;
                        }
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

        // Tests every ENABLED indexer and download client once, in one call. Bounded: at most 4
        // outbound tests run at once, each capped at 30s, the whole batch at 120s, and one failure
        // never stops the others. Each result updates only its own health row. No polling, no
        // background work — the caller re-reads the lists when this returns.
        api.MapPost("/connections/test-all", async (IDbContextFactory<OneArrDbContext> db, IHttpClientFactory httpFactory, IDownloadClient downloadClient, NativeTrackerClient native, CancellationToken ct) =>
        {
            var results = await TestAllCoreAsync(db, httpFactory, downloadClient, TimeSpan.FromSeconds(120), ct, native);
            return Results.Ok(new
            {
                results = results.Select(r => new { kind = r.Kind, id = r.Id, name = r.Name, ok = r.Ok, detail = r.Detail }),
                tested = results.Length,
                passed = results.Count(r => r.Ok),
            });
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
        changes = result.Changes.Select(c => new { c.TmdbId, c.Title, c.Type, c.SeriesMonitoredBefore, c.SeriesMonitoredAfter, c.MonitoredChanged, c.SeasonsChanged, c.EpisodesChanged, c.EpisodesUnknown, seasonChanges = c.SeasonChanges.Select(s => new { s.SeasonNumber, s.Before, s.After }), episodeChanges = c.EpisodeChanges.Select(e => new { e.SeasonNumber, e.EpisodeNumber, e.Before, e.After }) }),
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
            // Counts alone are not enough: the exact seasons and episodes, with their old and proposed
            // values, must match, or a preview for episodes 1 and 2 would pass after the source moved to 3 and 4.
            var freshSeasons = (f.SeasonChanges ?? []).ToDictionary(s => s.SeasonNumber, s => (s.Before, s.After));
            var previewSeasons = (p.SeasonChanges ?? []).ToDictionary(s => s.SeasonNumber, s => (s.Before, s.After));
            if (freshSeasons.Count != previewSeasons.Count) return false;
            foreach (var (season, values) in freshSeasons)
                if (!previewSeasons.TryGetValue(season, out var pv) || pv != values) return false;
            var freshEpisodes = (f.EpisodeChanges ?? []).ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber), e => (e.Before, e.After));
            var previewEpisodes = (p.EpisodeChanges ?? []).ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber), e => (e.Before, e.After));
            if (freshEpisodes.Count != previewEpisodes.Count) return false;
            foreach (var (episode, values) in freshEpisodes)
                if (!previewEpisodes.TryGetValue(episode, out var pv) || pv != values) return false;
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

    private static int? ParseProfileId(string? stored) =>
        stored is not null && int.TryParse(stored, out var id) ? id : null;

    private static async Task<int> RequireProfileAsync(IDbContextFactory<OneArrDbContext> db, int profileId, CancellationToken ct)
    {
        await using var c = await db.CreateDbContextAsync(ct);
        if (!await c.QualityProfiles.AnyAsync(p => p.Id == profileId, ct)) throw new ArgumentException("Quality profile does not exist");
        return profileId;
    }

    // Batch test core, extracted from the endpoint so tests can drive it with a short
    // deadline. Every item always yields a result: the gate wait sits INSIDE the per-item
    // handler, so a batch-deadline expiry while tests are queued reports those tests as
    // skipped instead of faulting Task.WhenAll — and the gate is only released when it was
    // actually acquired. Timeout classification uses the item deadline token, not the
    // exception type or its casing, because the shared retry helper converts
    // cancellation into ExternalServiceException("Timed out talking to ...").
    // Failure-path health writes are best-effort: a dead database must not turn a
    // per-item result into a fault.
    internal static async Task<(string Kind, int Id, string Name, bool Ok, string Detail)[]> TestAllCoreAsync(
        IDbContextFactory<OneArrDbContext> db, IHttpClientFactory httpFactory, IDownloadClient downloadClient,
        TimeSpan batchTimeout, CancellationToken ct, NativeTrackerClient? native = null)
    {
        await using var c = await db.CreateDbContextAsync(ct);
        var indexers = await c.Indexers.AsNoTracking().Where(i => i.Enabled).OrderBy(i => i.Priority).ThenBy(i => i.Id).ToListAsync(ct);
        var clients = await c.DownloadClients.AsNoTracking().Where(x => x.Enabled).OrderBy(x => x.Id).ToListAsync(ct);
        using var gate = new SemaphoreSlim(4);
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        batchCts.CancelAfter(batchTimeout);
        var batchCt = batchCts.Token;

        async Task<(string Kind, int Id, string Name, bool Ok, string Detail)> TestOneIndexerAsync(IndexerConfig indexer)
        {
            var acquired = false;
            var started = false;
            CancellationToken itemToken = default;
            try
            {
                await gate.WaitAsync(batchCt);
                acquired = true;
                started = true;
                using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(batchCt);
                itemCts.CancelAfter(TimeSpan.FromSeconds(30));
                itemToken = itemCts.Token;
                var (ok, detail) = TrackerSecretBox.IsNative(indexer.ApiKey)
                    ? (true, await (native ?? throw new ExternalServiceException("Native tracker engine unavailable")).TestAsync(indexer, itemCts.Token))
                    : await TestPlainIndexerAsync(httpFactory, indexer, itemCts.Token);
                // A fresh context per item: DbContext is not thread-safe across concurrent tests.
                await using var hc = await db.CreateDbContextAsync(batchCt);
                await UpsertIndexerHealthAsync(hc, indexer.Id, ok, ok ? null : detail, batchCt);
                return ("indexer", indexer.Id, indexer.Name, ok, detail);
            }
            catch (Exception ex)
            {
                // Timeout classification uses deadline state, not exception type or text:
                // the shared retry helper converts cancellation into
                // ExternalServiceException("Timed out talking to ..."), so checking for
                // OperationCanceledException alone misses real timeouts. A started item
                // whose deadline token fired timed out; a started item with a live token
                // failed genuinely; a queued item never started and was skipped.
                var detail = !started
                    ? $"Indexer '{indexer.Name}' skipped: batch deadline reached before the test started."
                    : itemToken.IsCancellationRequested
                        ? $"Indexer '{indexer.Name}' timed out."
                        : OutboundHttp.Redact(ex.Message);
                try
                {
                    await using var hc = await db.CreateDbContextAsync(CancellationToken.None);
                    await UpsertIndexerHealthAsync(hc, indexer.Id, false, detail, CancellationToken.None);
                }
                catch { /* best-effort: the result below still reports the outcome */ }
                return ("indexer", indexer.Id, indexer.Name, false, detail);
            }
            finally { if (acquired) gate.Release(); }
        }

        async Task<(string Kind, int Id, string Name, bool Ok, string Detail)> TestOneClientAsync(DownloadClientConfig client)
        {
            var acquired = false;
            var started = false;
            CancellationToken itemToken = default;
            try
            {
                await gate.WaitAsync(batchCt);
                acquired = true;
                started = true;
                using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(batchCt);
                itemCts.CancelAfter(TimeSpan.FromSeconds(30));
                itemToken = itemCts.Token;
                var (ok, detail, _, _) = await TestQbitAsync(downloadClient, client, itemCts.Token);
                // A fresh context per item: DbContext is not thread-safe across concurrent tests.
                await using var hc = await db.CreateDbContextAsync(batchCt);
                await UpsertClientHealthAsync(hc, client.Id, ok, ok ? null : detail, batchCt);
                return ("client", client.Id, client.Name, ok, detail);
            }
            catch (Exception ex)
            {
                // Same deadline-state classification as indexers: the retry helper turns
                // cancellation into ExternalServiceException, so the token — not the
                // exception type or its casing — decides timeout vs genuine failure.
                var detail = !started
                    ? $"Download client '{client.Name}' skipped: batch deadline reached before the test started."
                    : itemToken.IsCancellationRequested
                        ? $"Download client '{client.Name}' timed out."
                        : OutboundHttp.Redact(ex.Message);
                try
                {
                    await using var hc = await db.CreateDbContextAsync(CancellationToken.None);
                    await UpsertClientHealthAsync(hc, client.Id, false, detail, CancellationToken.None);
                }
                catch { /* best-effort: the result below still reports the outcome */ }
                return ("client", client.Id, client.Name, false, detail);
            }
            finally { if (acquired) gate.Release(); }
        }

        var tasks = indexers.Select(TestOneIndexerAsync).Concat(clients.Select(TestOneClientAsync)).ToList();
        return await Task.WhenAll(tasks);
    }

    // Secrets are never in these DTOs: only a hasKey/hasPassword flag. Health status is derived so a
    // disabled row always reads Disabled even if an old test result is stored, and a row with no
    // test row reads Not tested rather than a stale-looking value.
    private static string CreateNativeSecret(string trackerId, Dictionary<string, string>? fields,
        TrackerSecretBox secrets, string? existing, string baseUrl)
    {
        if (!NativeTrackerCredentials.IsSupported(trackerId))
            throw new ArgumentException("This tracker does not have a native OneArr adapter yet");
        _ = NativeTrackerClient.ValidateSiteUrl(baseUrl);
        NativeTrackerCredentials? old = TrackerSecretBox.IsNative(existing) ? secrets.Read(existing!) : null;
        if (old is not null && old.TrackerId != trackerId) old = null;
        string Field(string name) => fields is not null && fields.TryGetValue(name, out var v) ? v.Trim() : "";
        var username = Field("username");
        var password = Field("password");
        var altToken = Field("alt2fatoken");
        if (username.Length == 0) username = old?.Username ?? "";
        if (password.Length == 0) password = old?.Password ?? "";
        if (altToken.Length == 0) altToken = old?.Alt2FaToken ?? "";
        return secrets.Protect(new NativeTrackerCredentials(trackerId, username, password, altToken));
    }

    private static object IndexerDto(IndexerConfig i, IndexerHealth? h) => new
    {
        i.Id, i.Name, i.BaseUrl,
        host = HostOf(i.BaseUrl),
        protocol = TrackerSecretBox.IsNative(i.ApiKey) ? "TorrentLeech" :
            PublicRssClient.IsPublicRss(i.ApiKey) ? "RSS" :
            Uri.TryCreate(i.BaseUrl, UriKind.Absolute, out var nyaaUrl) && TorznabClient.IsDirectNyaa(nyaaUrl) ? "Nyaa RSS" : "Torznab",
        i.Enabled, i.MoviesEnabled, i.TvEnabled, i.Priority,
        hasKey = i.ApiKey.Length > 0 && !PublicRssClient.IsPublicRss(i.ApiKey),
        healthStatus = !i.Enabled ? "Disabled" : h is null ? "Not tested" : h.LastStatus ?? "Not tested",
        lastTestedAt = h?.LastTestedAt,
        lastError = h?.LastError,
    };

    private static object ClientDto(DownloadClientConfig d, DownloadClientHealth? h) => new
    {
        d.Id, d.Name, d.BaseUrl,
        host = HostOf(d.BaseUrl),
        type = "qBittorrent",
        d.Username, d.MovieCategory, d.TvCategory, d.Enabled,
        hasPassword = d.Password.Length > 0,
        healthStatus = !d.Enabled ? "Disabled" : h is null ? "Not tested" : h.LastError is not null ? "Failed" : h.LastSeenAt is not null ? "Connected" : "Not tested",
        lastSeenAt = h?.LastSeenAt,
        lastCheckedAt = h?.UpdatedAt,
        lastError = h?.LastError,
    };

    private static string HostOf(string baseUrl) =>
        Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ? uri.Host : "";

    // Health writes are best-effort diagnostics: a failed write must never change a test's outcome,
    // and lastTestedAt on the read side makes any staleness visible instead of silent.
    private static async Task UpsertIndexerHealthAsync(OneArrDbContext c, int indexerId, bool ok, string? error, CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var h = await c.IndexerHealth.FindAsync([indexerId], ct);
            if (h is null) { h = new IndexerHealth { IndexerId = indexerId }; c.IndexerHealth.Add(h); }
            h.LastTestedAt = now;
            h.LastStatus = ok ? "Healthy" : "Failed";
            h.LastError = ok ? null : error;
            h.UpdatedAt = now;
            await c.SaveChangesAsync(ct);
        }
        catch { /* diagnostic only; the test result itself stands */ }
    }

    private static async Task UpsertClientHealthAsync(OneArrDbContext c, int clientId, bool ok, string? error, CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var h = await c.DownloadClientHealth.FindAsync([clientId], ct);
            if (h is null) { h = new DownloadClientHealth { DownloadClientId = clientId }; c.DownloadClientHealth.Add(h); }
            if (ok) h.LastSeenAt = now;
            h.LastError = ok ? null : error;
            h.UpdatedAt = now;
            await c.SaveChangesAsync(ct);
        }
        catch { /* diagnostic only; the test result itself stands */ }
    }

    // Shared by /connections/test and /connections/test-all so a single test and a batch test
    // cannot drift apart. Throws ExternalServiceException (already redacted) on failure.
    private static async Task<(bool Ok, string Detail, TorznabCaps? Caps)> TestTorznabAsync(IHttpClientFactory httpFactory, string name, string baseUrl, string apiKey, CancellationToken ct)
    {
        var baseUri = UrlGuard.RequireServiceUrl(baseUrl);
        var http = httpFactory.CreateClient(OutboundHttp.TorznabClient);
        http.BaseAddress = new Uri(baseUri, "/");
        if (TorznabClient.IsDirectNyaa(baseUri))
        {
            using var rss = await OutboundHttp.SendWithRetryAsync(http,
                () => new HttpRequestMessage(HttpMethod.Get, TorznabClient.NyaaSearchPath(baseUri, "OneArr")), ct);
            if (!rss.IsSuccessStatusCode)
                return (false, $"Nyaa RSS returned HTTP {(int)rss.StatusCode}", null);
            TorznabClient.ParseNyaaRss(await rss.Content.ReadAsStringAsync(ct), name);
            return (true, "Nyaa RSS is reachable and returns a valid feed.", new TorznabCaps(
                [new TorznabCategory(5000, "TV / Anime")], true, true, false));
        }
        if (PublicRssClient.IsPublicRss(apiKey))
        {
            // Both media types parse the same feed. The test is read-only and
            // checks that a structurally valid RSS/Atom document is returned.
            var feed = new PublicRssClient(httpFactory);
            var entries = await feed.FetchAsync(baseUrl, name, MediaType.Series, ct);
            return (true, $"RSS feed is reachable ({entries.Count} recent torrent entries). RSS supplies recent releases but cannot search archives.",
                new TorznabCaps([new TorznabCategory(5000, "TV / RSS"),
                    new TorznabCategory(2000, "Movies / RSS")], true, true, true));
        }
        using var response = await OutboundHttp.SendWithRetryAsync(http,
            () => new HttpRequestMessage(HttpMethod.Get, "api?t=caps&apikey=" + Uri.EscapeDataString(apiKey)), ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? $"Indexer '{name}' rejected the API key (HTTP {(int)response.StatusCode}). Check the key."
                : $"Indexer '{name}' returned HTTP {(int)response.StatusCode}.";
            return (false, detail, null);
        }
        var caps = TorznabClient.ParseCaps(await response.Content.ReadAsStringAsync(ct), name);
        return (true, $"Indexer '{name}' answered caps with {caps.Categories.Count} categor{(caps.Categories.Count == 1 ? "y" : "ies")}.", caps);
    }

    private static string? ValidateProfileBody(ProfileBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 80)
            return "Quality profile name must contain 1-80 characters.";
        if (body.MinResolution is not (480 or 720 or 1080 or 2160) ||
            body.CutoffResolution is not (480 or 720 or 1080 or 2160) ||
            body.CutoffResolution < body.MinResolution)
            return "Resolutions must be 480, 720, 1080 or 2160, with cutoff >= minimum.";
        return null;
    }

    private static async Task<(bool Ok, string Detail)> TestPlainIndexerAsync(
        IHttpClientFactory httpFactory, IndexerConfig indexer, CancellationToken ct)
    {
        var (ok, detail, _) = await TestTorznabAsync(httpFactory, indexer.Name,
            indexer.BaseUrl, indexer.ApiKey, ct);
        return (ok, detail);
    }

    // Read-only against the client: sign-in + category listing only. Never adds, pauses, resumes
    // or deletes torrents, and an empty category is not by itself a failure.
    private static async Task<(bool Ok, string Detail, IReadOnlyList<DownloadClientItem> Movies, IReadOnlyList<DownloadClientItem> Tv)> TestQbitAsync(IDownloadClient downloadClient, DownloadClientConfig client, CancellationToken ct)
    {
        var movies = await downloadClient.ListAsync(client, client.MovieCategory, ct);
        var tv = await downloadClient.ListAsync(client, client.TvCategory, ct);
        return (true, $"qBittorrent '{client.Name}' signed in and listed {movies.Count} torrent(s) in '{client.MovieCategory}' and {tv.Count} in '{client.TvCategory}'.", movies, tv);
    }
}
