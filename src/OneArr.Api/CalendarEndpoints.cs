using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Api;

/// <summary>Upcoming and recent episodes of monitored series, and movie release dates, for the Calendar page.</summary>
public static class CalendarEndpoints
{
    public sealed record CalendarEntry(string Kind, int MediaItemId, int? EpisodeId, string Title, string? Detail,
        DateOnly Date, DateTimeOffset? AirsUtc, bool HasFile, bool Monitored, bool Downloading);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/calendar", async (DateOnly? from, DateOnly? to, IDbContextFactory<OneArrDbContext> db, TimeProvider time, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            var start = from ?? today.AddDays(-7);
            var end = to ?? today.AddDays(28);
            if (end < start || end.DayNumber - start.DayNumber > 120) throw new ArgumentException("Choose a range of at most 120 days");
            await using var c = await db.CreateDbContextAsync(ct);
            var downloading = (await c.DownloadJobs.AsNoTracking()
                .Where(j => j.Status == DownloadStatus.Queued || j.Status == DownloadStatus.Downloading || j.Status == DownloadStatus.Completed)
                .Select(j => new { j.MediaItemId, j.EpisodeId }).ToListAsync(ct))
                .Select(j => (j.MediaItemId, j.EpisodeId)).ToHashSet();

            var episodes = await (from e in c.Episodes.AsNoTracking()
                                  join m in c.MediaItems.AsNoTracking() on e.MediaItemId equals m.Id
                                  where m.Monitored && e.AirDate != null && e.AirDate >= start.AddDays(-1) && e.AirDate <= end
                                  select new { e.Id, MediaItemId = m.Id, m.Title, e.SeasonNumber, e.EpisodeNumber, EpisodeTitle = e.Title, e.AirDate, e.AirDateUtc, e.HasFile, e.Monitored })
                                  .ToListAsync(ct);
            var entries = episodes
                .Select(e => new CalendarEntry("episode", e.MediaItemId, e.Id, e.Title,
                    $"S{e.SeasonNumber:00}E{e.EpisodeNumber:00}" + (string.IsNullOrWhiteSpace(e.EpisodeTitle) ? "" : " · " + e.EpisodeTitle),
                    e.AirDateUtc is DateTimeOffset utc ? DateOnly.FromDateTime(utc.UtcDateTime) : e.AirDate!.Value, e.AirDateUtc, e.HasFile, e.Monitored,
                    downloading.Contains(((int?)e.MediaItemId, (int?)e.Id))))
                .Where(e => e.Date >= start && e.Date <= end)
                .ToList();

            var movies = await c.MediaItems.AsNoTracking().Where(m => m.MediaType == MediaType.Movie && m.Monitored)
                .Select(m => new { m.Id, m.Title, m.Year, m.TheatricalReleaseDate, m.DigitalReleaseDate, m.PhysicalReleaseDate, m.HasFile }).ToListAsync(ct);
            foreach (var m in movies)
                foreach (var (date, label) in new[] { (m.TheatricalReleaseDate, "In cinemas"), (m.DigitalReleaseDate, "Digital release"), (m.PhysicalReleaseDate, "Disc release") })
                    if (date is DateOnly d && d >= start && d <= end)
                        entries.Add(new CalendarEntry("movie", m.Id, null, m.Year is int y ? $"{m.Title} ({y})" : m.Title, label, d, null, m.HasFile, true,
                            downloading.Contains(((int?)m.Id, (int?)null))));

            return Results.Ok(entries.OrderBy(e => e.Date).ThenBy(e => e.AirsUtc ?? DateTimeOffset.MaxValue).ThenBy(e => e.Title).ToList());
        });
    }
}
