using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Services;

/// <summary>
/// Bounded background runner. Each named job has a single-permit lock, so a scheduled run and a manual run
/// can never overlap. Every run is written to JobHistory. Failures are recorded, not thrown into the host.
/// </summary>
public sealed class JobRunner
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly IDbContextFactory<OneArrDbContext> _db;
    private readonly TimeProvider _time;

    public JobRunner(IDbContextFactory<OneArrDbContext> db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <summary>Runs the job unless another instance of the same job is already running. Returns false if skipped.</summary>
    public async Task<bool> TryRunAsync(string name, Func<CancellationToken, Task<string>> work, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return false;
        var historyId = 0;
        try
        {
            await using (var db = await _db.CreateDbContextAsync(ct))
            {
                var history = new JobHistory { Name = name, StartedAt = _time.GetUtcNow() };
                db.JobHistories.Add(history);
                await db.SaveChangesAsync(ct);
                historyId = history.Id;
            }

            string message;
            var success = true;
            try
            {
                message = await work(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                success = false;
                message = ex.GetType().Name + ": " + ex.Message;
            }

            await using var done = await _db.CreateDbContextAsync(CancellationToken.None);
            var row = await done.JobHistories.FirstAsync(h => h.Id == historyId, CancellationToken.None);
            row.FinishedAt = _time.GetUtcNow();
            row.Success = success;
            row.Message = Truncate(message);
            await done.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Truncate(string s) => s.Length <= 1000 ? s : s[..1000];
}
