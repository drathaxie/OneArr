using Microsoft.EntityFrameworkCore;
using OneArr.Domain;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Infrastructure.Services;

public sealed class SettingsStore
{
    private readonly IDbContextFactory<OneArrDbContext> _db;

    public SettingsStore(IDbContextFactory<OneArrDbContext> db)
    {
        _db = db;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return (await db.Settings.FindAsync([key], ct))?.Value;
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default) =>
        bool.TryParse(await GetAsync(key, ct), out var b) ? b : defaultValue;

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var existing = await db.Settings.FindAsync([key], ct);
        if (value is null)
        {
            if (existing is not null) db.Settings.Remove(existing);
        }
        else if (existing is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Writes several settings in one context and one SaveChanges, so a multi-field settings save
    /// commits together instead of one row at a time. Callers must validate every value BEFORE calling
    /// this: anything that throws here after SaveChanges has already changed earlier rows.
    /// </summary>
    public async Task SetManyAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        foreach (var (key, value) in values)
        {
            var existing = await db.Settings.FindAsync([key], ct);
            if (value is null)
            {
                if (existing is not null) db.Settings.Remove(existing);
            }
            else if (existing is null)
            {
                db.Settings.Add(new AppSetting { Key = key, Value = value });
            }
            else
            {
                existing.Value = value;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
