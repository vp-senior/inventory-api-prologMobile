using System.Globalization;
using System.Net;
using System.Text.Json;
using Inventory.Core.Results;
using Microsoft.Extensions.Logging;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Inventory.Infrastructure.Http;

/// <summary>
/// The only class that speaks HTTP to the Inventory API. It turns every outcome of a request,
/// good or bad, into a <see cref="Result{T}"/>; retries and pacing happen in the handler pipeline
/// configured in <see cref="InventoryApiResilience"/>, so by the time a failure is seen here it is final.
/// </summary>
internal sealed class InventoryApiClient(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<InventoryApiClient> logger) : IInventoryApiClient
{
    public const string HttpClientName = "InventoryApi";

    private const int MaxLoggedBodyLength = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<RecordsPageDto>> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        // A client is created per call, as IHttpClientFactory intends: it is cheap, and the pooled
        // handler underneath is reused and recycled by the factory. Holding on to one HttpClient
        // from this singleton would pin a handler forever and miss DNS changes.
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var requestUri = string.Create(CultureInfo.InvariantCulture, $"api/records?page={page}&pageSize={pageSize}");

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return response.StatusCode == HttpStatusCode.OK
                ? ParsePage(body, page)
                : DescribeErrorStatus(response, body, page);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Inventory API page {Page}: request failed at the transport level ({Error})", page, exception.HttpRequestError);
            return Failure(InventoryFailureKind.Unavailable, "The inventory API could not be reached.");
        }
        catch (TimeoutRejectedException exception)
        {
            logger.LogWarning(exception, "Inventory API page {Page}: no usable answer within {Timeout}", page, exception.Timeout);
            return Failure(InventoryFailureKind.Timeout, "The inventory API did not answer in time.");
        }
        catch (RateLimiterRejectedException exception)
        {
            logger.LogWarning(exception, "Inventory API page {Page}: too many requests are already waiting for a rate-limit permit", page);
            return Failure(InventoryFailureKind.Unavailable, "Too many requests to the inventory API are already queued.", exception.RetryAfter);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancelled, but not by our caller: HttpClient reports its own internal timeouts this way.
            logger.LogWarning(exception, "Inventory API page {Page}: request was cancelled by the HTTP stack", page);
            return Failure(InventoryFailureKind.Timeout, "The inventory API did not answer in time.");
        }
    }

    private Result<RecordsPageDto> ParsePage(string body, int page)
    {
        try
        {
            if (JsonSerializer.Deserialize<RecordsPageDto>(body, JsonOptions) is { } dto)
            {
                return dto;
            }

            logger.LogWarning("Inventory API page {Page}: HTTP 200 with a JSON null body", page);
            return Failure(InventoryFailureKind.InvalidData, $"The inventory API returned an empty body for page {page}.");
        }
        catch (JsonException exception)
        {
            logger.LogWarning(
                exception,
                "Inventory API page {Page}: HTTP 200 but the body is not the expected JSON (at {JsonPath}). Body starts: {Body}",
                page, exception.Path, Truncate(body));
            return Failure(
                InventoryFailureKind.InvalidData,
                $"The inventory API returned a body for page {page} that could not be read as the documented JSON (at '{exception.Path}').");
        }
    }

    private Result<RecordsPageDto> DescribeErrorStatus(HttpResponseMessage response, string body, int page)
    {
        var status = (int)response.StatusCode;
        var retryAfter = UpstreamResponse.GetRetryAfter(response, timeProvider);

        logger.LogWarning(
            "Inventory API page {Page}: HTTP {StatusCode} (Retry-After: {RetryAfter}). Body starts: {Body}",
            page, status, retryAfter, Truncate(body));

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failure(
                InventoryFailureKind.Rejected,
                $"The inventory API rejected this service's credentials (HTTP {status}). Check the configured API key."),

            HttpStatusCode.TooManyRequests => Failure(
                InventoryFailureKind.Unavailable,
                "The inventory API is rate limiting this service (HTTP 429).",
                retryAfter),

            _ when UpstreamResponse.IsTransient(response.StatusCode) => Failure(
                InventoryFailureKind.Unavailable,
                $"The inventory API kept failing with HTTP {status}.",
                retryAfter),

            // 400, 404, an unexpected 204 or 3xx, or anything else nobody has seen yet:
            // not something a retry fixes, and not something to guess at.
            _ => Failure(
                InventoryFailureKind.Rejected,
                $"The inventory API answered the request for page {page} with an unexpected HTTP {status}."),
        };
    }

    private static InventoryFailure Failure(InventoryFailureKind kind, string message, TimeSpan? retryAfter = null) =>
        new(kind, message) { RetryAfter = retryAfter };

    private static string Truncate(string body) =>
        body.Length <= MaxLoggedBodyLength ? body : string.Concat(body.AsSpan(0, MaxLoggedBodyLength), "…");
}
