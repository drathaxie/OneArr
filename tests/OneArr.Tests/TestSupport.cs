using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OneArr.Infrastructure.Persistence;

namespace OneArr.Tests;

/// <summary>Routes outbound HTTP to a delegate so tests never touch the network.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }

    public static HttpResponseMessage Text(string body, HttpStatusCode code = HttpStatusCode.OK, string contentType = "application/json") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, contentType) };
}

public sealed class FakeFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public FakeFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };
}

public static class TestDb
{
    /// <summary>Shared in-memory SQLite. Keep the returned connection open for the life of the test.</summary>
    public static (IDbContextFactory<OneArrDbContext> Factory, Microsoft.Data.Sqlite.SqliteConnection Keepalive) Create()
    {
        var name = "file:test_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared";
        var keep = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + name);
        keep.Open();
        var services = new ServiceCollection();
        services.AddDbContextFactory<OneArrDbContext>(o => o.UseSqlite("Data Source=" + name));
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<OneArrDbContext>>();
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }
        return (factory, keep);
    }
}
