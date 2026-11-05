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
    public DbSet<IndexerHealth> IndexerHealth => Set<IndexerHealth>();
    public DbSet<DownloadClientConfig> DownloadClients => Set<DownloadClientConfig>();
    public DbSet<DownloadJob> DownloadJobs => Set<DownloadJob>();
    public DbSet<DownloadClientHealth> DownloadClientHealth => Set<DownloadClientHealth>();
    public DbSet<ImportPreview> ImportPreviews => Set<ImportPreview>();
    public DbSet<ImportRecord> ImportRecords => Set<ImportRecord>();
    public DbSet<FileRecord> FileRecords => Set<FileRecord>();
    public DbSet<JobHistory> JobHistories => Set<JobHistory>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<SearchAttempt> SearchAttempts => Set<SearchAttempt>();
    public DbSet<LibraryRoot> LibraryRoots => Set<LibraryRoot>();
    public DbSet<PathMapping> PathMappings => Set<PathMapping>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<MediaFileEpisode> MediaFileEpisodes => Set<MediaFileEpisode>();
    public DbSet<LibraryScanPlan> LibraryScanPlans => Set<LibraryScanPlan>();

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
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.MediaItemId, x.EpisodeId, x.Status });
            e.HasIndex(x => x.DownloadClientId);
            e.Property(x => x.MediaType).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<DownloadClientHealth>(e =>
        {
            e.HasKey(x => x.DownloadClientId);
        });

        b.Entity<IndexerHealth>(e =>
        {
            e.HasKey(x => x.IndexerId);
        });

        b.Entity<ImportPreview>(e =>
        {
            e.HasIndex(x => x.DownloadJobId);
        });

        b.Entity<ImportRecord>(e =>
        {
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<SearchAttempt>(e =>
        {
            e.HasIndex(x => x.ItemKey).IsUnique();
            e.HasIndex(x => x.NextAttemptAt);
            e.Property(x => x.LastOutcome).HasConversion<string>().HasMaxLength(32);
        });

        b.Entity<LibraryRoot>(e =>
        {
            e.HasIndex(x => new { x.MediaType, x.PathKey }).IsUnique();
            e.Property(x => x.MediaType).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LastState).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<PathMapping>(e =>
        {
            e.HasIndex(x => new { x.MediaType, x.SourcePrefix }).IsUnique();
            e.Property(x => x.MediaType).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<MediaFile>(e =>
        {
            e.HasIndex(x => x.PathKey).IsUnique();
            e.HasIndex(x => x.MediaItemId);
            e.HasIndex(x => x.LibraryRootId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<MediaItem>().WithMany().HasForeignKey(x => x.MediaItemId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<LibraryRoot>().WithMany().HasForeignKey(x => x.LibraryRootId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MediaFileEpisode>(e =>
        {
            e.HasKey(x => new { x.MediaFileId, x.EpisodeId });
            e.HasIndex(x => x.EpisodeId);
            e.HasOne(x => x.MediaFile).WithMany(f => f.EpisodeLinks).HasForeignKey(x => x.MediaFileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FileRecord>(e => e.HasIndex(x => x.Path).IsUnique());
        b.Entity<AppSetting>(e => e.HasKey(x => x.Key));
    }
}
