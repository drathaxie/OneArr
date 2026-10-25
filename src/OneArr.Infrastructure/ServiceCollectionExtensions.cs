using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using OneArr.Application;
using OneArr.Domain;
using OneArr.Infrastructure.Files;
using OneArr.Infrastructure.Http;
using OneArr.Infrastructure.Legacy;
using OneArr.Infrastructure.Persistence;
using OneArr.Infrastructure.Providers;
using OneArr.Infrastructure.Services;

namespace OneArr.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOneArrInfrastructure(this IServiceCollection services, string databasePath, string? tmdbFallbackKey, Uri? tmdbBaseUri = null)
    {
        services.AddDbContextFactory<OneArrDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));

        // Every outbound client has a finite timeout; retries live in OutboundHttp.
        services.AddHttpClient(OutboundHttp.TmdbClient, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient(OutboundHttp.TorznabClient, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(OutboundHttp.QbittorrentClient, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient(OutboundHttp.LegacyClient, c => c.Timeout = TimeSpan.FromSeconds(20));

        services.AddSingleton<IReleaseParser, ReleaseParser>();
        services.AddSingleton<IReleaseRanker, ReleaseRanker>();
        services.AddSingleton<IIndexerClient, TorznabClient>();
        services.AddSingleton<IDownloadClient, QBittorrentClient>();
        services.AddSingleton<IImporter, SafeImporter>();
        services.AddSingleton<ILibraryScanner, LibraryScanner>();
        services.AddSingleton<ILegacyArrReader, LegacyArrReader>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddSingleton<SettingsStore>();
        services.AddSingleton<IMetadataProvider>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsStore>();
            return new TmdbMetadataProvider(
                sp.GetRequiredService<IHttpClientFactory>(),
                async () => await settings.GetAsync(SettingKeys.TmdbApiKey) is { Length: > 0 } stored ? stored : tmdbFallbackKey,
                tmdbBaseUri);
        });
        services.AddSingleton<MediaService>();
        services.AddSingleton<ReleaseService>();
        services.AddSingleton<DownloadService>();
        services.AddSingleton<JobRunner>();
        services.AddSingleton(new SearchPolicy());
        services.AddSingleton<WantedSearchService>();
        services.AddHostedService<BackgroundScheduler>();
        return services;
    }
}
