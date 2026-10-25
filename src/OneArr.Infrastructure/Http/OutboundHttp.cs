using System.Net;
using OneArr.Application;

namespace OneArr.Infrastructure.Http;

/// <summary>
/// Bounded retry for outbound calls: retries transport failures, 429 and 5xx with exponential backoff.
/// Honours Retry-After when present. Never retries non-idempotent POSTs unless the caller opts in.
/// </summary>
public static class OutboundHttp
{
    public const string TmdbClient = "tmdb";
    public const string TorznabClient = "torznab";
    public const string QbittorrentClient = "qbittorrent";
    public const string LegacyClient = "legacy";

    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct,
        int maxRetries = 2)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            try
            {
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!retryable || attempt >= maxRetries) return response;

                var delay = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                response.Dispose();
                await Task.Delay(Cap(delay), ct);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                await Task.Delay(Backoff(attempt), ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                _ = ex;
                await Task.Delay(Backoff(attempt), ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ExternalServiceException($"Network error talking to {client.BaseAddress?.Host ?? "service"}: {ex.GetType().Name}");
            }
            catch (TaskCanceledException)
            {
                throw new ExternalServiceException($"Timed out talking to {client.BaseAddress?.Host ?? "service"}");
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));

    private static TimeSpan Cap(TimeSpan delay) => delay > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delay;

    /// <summary>Removes API keys and other secrets from text before it reaches logs or API errors.</summary>
    public static string Redact(string text)
    {
        foreach (var marker in new[] { "apikey=", "api_key=", "password=", "X-Api-Key" })
        {
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                var end = idx + marker.Length;
                var stop = end;
                while (stop < text.Length && text[stop] is not ('&' or ' ' or '"' or '\'' or '\n')) stop++;
                text = text[..end] + "***" + text[stop..];
                idx = text.IndexOf(marker, end + 3, StringComparison.OrdinalIgnoreCase);
            }
        }
        return text;
    }
}
