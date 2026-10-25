using Microsoft.EntityFrameworkCore;
using OneArr.Domain;

namespace OneArr.Infrastructure.Persistence;

public class OneArrDbContext : DbContext
{
    public OneArrDbContext(DbContextOptions<OneArrDbContext> options) : base(options) { }

    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<QualityProfile> QualityProfiles => Set<QualityProfile>();
    public DbSet<IndexerConfig> Indexers => Set<IndexerConfig>();
    public DbSet<DownloadClientConfig> DownloadClients => Set<DownloadClientConfig>();
    public DbSet<DownloadJob> DownloadJobs => Set<DownloadJob>();
    public DbSet<ImportRecord> ImportRecords => Set<ImportRecord>();
    public DbSet<FileRecord> FileRecords => Set<FileRecord>();
    public DbSet<JobHistory> JobHistories => Set<JobHistory>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MediaItem>(e =>
        {
            e.HasIndex(x => new { x.MediaType, x.Provider, x.ProviderId }).IsUnique();
            e.Property(x => x.MediaType).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ProviderId).HasMaxLength(64);
            e.HasOne(x => x.QualityProfile).WithMany().HasForeignKey(x => x.QualityProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Season>(e =>
        {
            e.HasIndex(x => new { x.MediaItemId, x.SeasonNumber }).IsUnique();
            e.HasOne<MediaItem>().WithMany(m => m.Seasons).HasForeignKey(x => x.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Episode>(e =>
        {
            e.HasIndex(x => new { x.MediaItemId, x.SeasonNumber, x.EpisodeNumber }).IsUnique();
            e.HasIndex(x => new { x.Provider, x.ProviderId });
            e.HasOne<MediaItem>().WithMany(m => m.Episodes).HasForeignKey(x => x.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DownloadJob>(e =>
        {
            e.HasIndex(x => x.Hash);
            e.Property(x => x.MediaType).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<ImportRecord>(e =>
        {
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<FileRecord>(e => e.HasIndex(x => x.Path).IsUnique());
        b.Entity<AppSetting>(e => e.HasKey(x => x.Key));
    }
}
