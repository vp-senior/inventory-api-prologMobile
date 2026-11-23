using System.Threading.RateLimiting;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Inventory.Infrastructure.Http;

/// <summary>
/// The resilience pipeline wrapped around every request to the Inventory API.
/// Each strategy is configured explicitly here rather than taken from a preset, so that what
/// happens on a failure can be read in one place. Strategies run outermost first:
///
///   request timeout  ->  retry  ->  rate limiter  ->  attempt timeout  ->  HTTP call
/// </summary>
internal static class InventoryApiResilience
{
    public const string PipelineName = "inventory-api";

    private const int RateLimiterSegments = 10;

    public static void Configure(
        ResiliencePipelineBuilder<HttpResponseMessage> pipeline,
        InventoryApiOptions options,
        RateLimiter rateLimiter,
        TimeProvider timeProvider)
    {
        // Lets tests drive backoff delays and timeouts with a fake clock instead of real waiting.
        pipeline.TimeProvider = timeProvider;

        // 1. Upper bound for one page, covering every attempt, every backoff and any wait for a
        //    rate-limit permit. Without it a slow upstream could hold a caller for a very long time.
        pipeline.AddTimeout(options.RequestTimeout);

        // 2. Retry transient failures only. Exponential backoff with jitter spreads retries out;
        //    if the server names a wait in Retry-After, that takes precedence over our own backoff.
        pipeline.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = options.MaxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = options.RetryBaseDelay,
            ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome, options, timeProvider)),
            DelayGenerator = args => ValueTask.FromResult(
                args.Outcome.Result is { } response ? UpstreamResponse.GetRetryAfter(response, timeProvider) : null),
        });

        // 3. Pace our own requests to the upstream's published limit, so that in normal operation we
        //    queue briefly instead of provoking 429s. Sits inside the retry so retries are paced too.
        pipeline.AddRateLimiter(rateLimiter);

        // 4. Bound each individual attempt. A timed-out attempt surfaces as TimeoutRejectedException,
        //    which the retry above treats as transient.
        pipeline.AddTimeout(options.AttemptTimeout);
    }

    /// <summary>
    /// A sliding window matching the upstream limit ("N requests per window"). Requests beyond the
    /// limit wait in a queue rather than fail.
    /// </summary>
    public static RateLimiter CreateRateLimiter(InventoryApiOptions options)
    {
        // The limiter frees permits when a whole segment leaves the window, which can be up to one
        // segment earlier than a full window after the request was sent. Making the window one
        // segment longer means a permit is reused after one to 1.1 upstream windows instead of 0.9
        // to one. Timer scheduling can still shave off a few milliseconds, and the upstream may
        // count differently than we assume; a 429 that slips through is handled by the retry.
        var segment = options.RateLimitWindow / RateLimiterSegments;

        return new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = options.RateLimitPermits,
            Window = options.RateLimitWindow + segment,
            SegmentsPerWindow = RateLimiterSegments + 1,
            QueueLimit = 100,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    private static bool ShouldRetry(Outcome<HttpResponseMessage> outcome, InventoryApiOptions options, TimeProvider timeProvider)
    {
        if (outcome.Exception is not null)
        {
            // Connection-level failures and attempt timeouts. Cancellation by the caller is not retried.
            return outcome.Exception is HttpRequestException or TimeoutRejectedException;
        }

        var response = outcome.Result!;
        if (!UpstreamResponse.IsTransient(response.StatusCode))
        {
            // Success, or an error that would repeat (400, 401, 403, 404...): hand it straight back.
            return false;
        }

        // If the server asks for a longer wait than we are prepared to hold a caller for, stop now
        // and report the wait to our own caller rather than retrying too early.
        return UpstreamResponse.GetRetryAfter(response, timeProvider) is not { } retryAfter
            || retryAfter <= options.MaxRetryAfter;
    }
}
