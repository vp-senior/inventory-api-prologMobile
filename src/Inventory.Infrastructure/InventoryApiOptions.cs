namespace Inventory.Infrastructure;

/// <summary>
/// Settings for talking to the upstream Inventory API, bound from the "InventoryApi" configuration section.
/// <see cref="BaseUrl"/> and <see cref="ApiKey"/> have no defaults on purpose: they must come from
/// user secrets or environment variables, never from source control.
/// </summary>
public sealed class InventoryApiOptions
{
    public const string SectionName = "InventoryApi";

    /// <summary>The largest pageSize the upstream API accepts.</summary>
    public const int MaxPageSize = 25;

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ApiKeyHeaderName { get; set; } = "x-api-key";

    /// <summary>Records requested per page. The maximum keeps the number of rate-limited calls lowest.</summary>
    public int PageSize { get; set; } = MaxPageSize;

    /// <summary>
    /// How many times one page may be requested before giving up when it answers 200 but its content
    /// fails validation. This is separate from the HTTP retries below, which deal with failed requests.
    /// </summary>
    public int MaxPageAttempts { get; set; } = 3;

    /// <summary>Retries after the first HTTP attempt, for transient failures only.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>First backoff delay; it doubles on each retry, with jitter.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest Retry-After we are willing to sit out. Anything longer fails fast instead.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Time allowed for a single HTTP attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Time allowed for one page including all retries, backoff and waiting for a rate-limit permit.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>The upstream allows this many requests per <see cref="RateLimitWindow"/>; we pace ourselves to match.</summary>
    public int RateLimitPermits { get; set; } = 5;

    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Time allowed to collect and verify the whole dataset.</summary>
    public TimeSpan SnapshotTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>How long a verified dataset is reused. Zero disables caching.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}
