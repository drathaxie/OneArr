using System.Net.Http.Headers;
using System.Text.Json;

namespace OneArr.Updates;

public sealed record ReleaseInfo(string Tag, Version Version, string ZipName, string ZipUrl, string ShaUrl);

/// <summary>
/// Reads the latest GitHub release of OneArr and picks its Windows x64 artifact. Only HTTPS URLs on GitHub
/// hosts are accepted, so a tampered API response cannot point the updater at another server.
/// </summary>
public sealed class ReleaseClient
{
    public const string Repo = "drathaxie/OneArr";
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
    };

    private readonly HttpClient _http;
    private readonly string _apiBase;

    /// <param name="apiBase">
    /// Normally https://api.github.com. A different base is a test hook (tray.json "updateApi"): then only URLs with the
    /// same scheme and host as that base are accepted, so the feed and its downloads stay on one server you chose.
    /// </param>
    public ReleaseClient(HttpClient http, string? apiBase = null)
    {
        _http = http;
        _apiBase = (apiBase ?? "https://api.github.com").TrimEnd('/');
    }

    private void EnsureAllowed(string url)
    {
        if (_apiBase == "https://api.github.com") { EnsureAllowedUrl(url); return; }
        var baseUri = new Uri(_apiBase);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != baseUri.Scheme || !uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update URL is not on the configured update server");
    }

    /// <returns>The latest release with a valid artifact, or null when the repository has no release.</returns>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/repos/{Repo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.ParseAdd("OneArr-Updater");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ReleaseVersions.Parse(tag) ?? throw new InvalidDataException($"Release tag '{tag}' is not a version");

        string? zipName = null, zipUrl = null, shaUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name == $"OneArr-{version}-win-x64.zip") { zipName = name; zipUrl = url; }
            else if (name == $"OneArr-{version}-win-x64.zip.sha256") shaUrl = url;
        }
        if (zipUrl is null || shaUrl is null) return null; // an incomplete release is not offered

        EnsureAllowed(zipUrl);
        EnsureAllowed(shaUrl);
        return new ReleaseInfo(tag, version, zipName!, zipUrl, shaUrl);
    }

    /// <summary>Downloads to a file, hashing as it streams, and refuses anything over maxBytes.</summary>
    public async Task<long> DownloadAsync(string url, string destination, long maxBytes, CancellationToken ct)
    {
        EnsureAllowed(url);
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength is long len && len > maxBytes)
            throw new InvalidDataException($"Download is {len} bytes, above the {maxBytes} byte limit");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var written = 0L;
        await using (var input = await res.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                written += n;
                if (written > maxBytes) throw new InvalidDataException("Download exceeded the size limit");
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        return written;
    }

    /// <summary>Reads the expected SHA-256 from a "hash  filename" checksum file.</summary>
    public async Task<string> ReadExpectedHashAsync(string shaUrl, string zipName, CancellationToken ct)
    {
        EnsureAllowed(shaUrl);
        var text = await _http.GetStringAsync(shaUrl, ct);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*') == zipName && parts[0].Length == 64)
                return parts[0].ToLowerInvariant();
        }
        throw new InvalidDataException($"Checksum file does not list {zipName}");
    }

    public static void EnsureAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Update URL must be HTTPS");
        if (!AllowedHosts.Contains(uri.Host))
            throw new InvalidDataException($"Update host '{uri.Host}' is not a GitHub host");
    }
}
