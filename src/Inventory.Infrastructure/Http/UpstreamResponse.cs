using System.Net;

namespace Inventory.Infrastructure.Http;

/// <summary>Shared answers to "what does this upstream response mean?", used by the retry policy and the client.</summary>
internal static class UpstreamResponse
{
    /// <summary>
    /// Status codes where the same request may well succeed if sent again: the server failed (5xx),
    /// timed out (408) or asked us to slow down (429). Any other error status would just fail again.
    /// </summary>
    public static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    /// <summary>
    /// Reads the Retry-After header, which may be a number of seconds or an HTTP date.
    /// Returns null when the header is absent or does not ask for a wait.
    /// </summary>
    public static TimeSpan? GetRetryAfter(HttpResponseMessage response, TimeProvider timeProvider)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date - timeProvider.GetUtcNow());

        return delay > TimeSpan.Zero ? delay : null;
    }
}
