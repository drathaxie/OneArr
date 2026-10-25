using OneArr.Application;

namespace OneArr.Infrastructure.Security;

/// <summary>
/// Validates administrator-configured service URLs. OneArr only calls URLs that were explicitly saved
/// by an authenticated administrator; this guard prevents obviously unsafe targets (file://, credentials
/// in URLs, cloud metadata endpoints) and never follows arbitrary URLs supplied by remote content.
/// </summary>
public static class UrlGuard
{
    public static Uri RequireServiceUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            throw new ExternalServiceException("Service URL must be an absolute http(s) URL");
        if (uri.Scheme is not ("http" or "https"))
            throw new ExternalServiceException("Service URL must use http or https");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ExternalServiceException("Put credentials in the dedicated fields, not in the URL");
        if (uri.Host is "169.254.169.254" || uri.Host.StartsWith("169.254.", StringComparison.Ordinal))
            throw new ExternalServiceException("Link-local metadata addresses are not allowed");
        return uri;
    }

    /// <summary>Accepts only magnet links or http(s) torrent URLs returned by an indexer.</summary>
    public static bool IsAllowedDownloadLink(string link)
    {
        if (link.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    }
}
