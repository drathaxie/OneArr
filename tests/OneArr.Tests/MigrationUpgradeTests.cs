using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OneArr.Infrastructure.Persistence;
using Xunit;

namespace OneArr.Tests;

/// <summary>Upgrades a database built by the previous release, the way the app does at startup, and checks that existing rows survive.</summary>
public class MigrationUpgradeTests
{
    [Fact]
    public async Task Upgrading_an_existing_database_keeps_every_row_and_adds_the_library_tables()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onearr-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new DbContextOptionsBuilder<OneArrDbContext>().UseSqlite($"Data Source={Path.Combine(dir, "onearr.db")}").Options;

            await using (var previous = new OneArrDbContext(options))
            {
                await ((IInfrastructure<IServiceProvider>)previous.Database).Instance.GetRequiredService<IMigrator>().MigrateAsync("AddSearchAttempts");
                await previous.Database.ExecuteSqlRawAsync("INSERT INTO QualityProfiles (Id, Name, MinResolution, CutoffResolution, AllowWebSources) VALUES (1, 'HD-1080p', 720, 1080, 1)");
                await previous.Database.ExecuteSqlRawAsync(
                    "INSERT INTO MediaItems (Id, MediaType, Provider, ProviderId, Title, Year, Monitored, QualityProfileId, RootFolder, HasFile, CreatedAt) " +
                    "VALUES (7, 'Movie', 'tmdb', '438631', 'Dune', 2021, 1, 1, 'D:\\Media\\Movies', 1, '2026-01-01 00:00:00+00:00')");
            }

            await using (var upgraded = new OneArrDbContext(options))
            {
                await upgraded.Database.MigrateAsync();

                var applied = await upgraded.Database.GetAppliedMigrationsAsync();
                Assert.Contains(applied, m => m.EndsWith("AddLibraryReconciliation", StringComparison.Ordinal));

                var movie = await upgraded.MediaItems.SingleAsync();
                Assert.Equal(7, movie.Id);
                Assert.Equal("Dune", movie.Title);
                Assert.True(movie.HasFile);
                Assert.Equal("D:\\Media\\Movies", movie.RootFolder);
                Assert.Null(movie.LegacyPath);
                // A catalog from before reconciliation is unreviewed after upgrade, so automatic grabbing stays blocked until a complete apply.
                Assert.Null(movie.LibraryReviewedUtc);
                Assert.Equal(1, await upgraded.QualityProfiles.CountAsync());
                Assert.Empty(await upgraded.LibraryRoots.ToListAsync());
                Assert.Empty(await upgraded.MediaFiles.ToListAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
