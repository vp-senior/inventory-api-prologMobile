using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure;

/// <summary>
/// Checked once at startup, so a missing key or a typo in configuration stops the application
/// with a clear message instead of surfacing later as a confusing upstream error.
/// </summary>
internal sealed class InventoryApiOptionsValidator : IValidateOptions<InventoryApiOptions>
{
    private const string HowToConfigure =
        "Set it with 'dotnet user-secrets' or an environment variable (see README, \"Setup and run\").";

    public ValidateOptionsResult Validate(string? name, InventoryApiOptions options)
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add($"InventoryApi:BaseUrl must be an absolute http(s) URL. {HowToConfigure}");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            errors.Add($"InventoryApi:ApiKey is not configured. {HowToConfigure}");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKeyHeaderName))
        {
            errors.Add("InventoryApi:ApiKeyHeaderName must not be empty.");
        }

        if (options.PageSize is < 1 or > InventoryApiOptions.MaxPageSize)
        {
            errors.Add($"InventoryApi:PageSize must be between 1 and {InventoryApiOptions.MaxPageSize}.");
        }

        if (options.MaxPageAttempts < 1)
        {
            errors.Add("InventoryApi:MaxPageAttempts must be at least 1.");
        }

        if (options.MaxRetryAttempts is < 1 or > 10)
        {
            errors.Add("InventoryApi:MaxRetryAttempts must be between 1 and 10.");
        }

        if (options.RateLimitPermits < 1)
        {
            errors.Add("InventoryApi:RateLimitPermits must be at least 1.");
        }

        RequirePositive(options.RetryBaseDelay, nameof(options.RetryBaseDelay));
        RequirePositive(options.MaxRetryAfter, nameof(options.MaxRetryAfter));
        RequirePositive(options.AttemptTimeout, nameof(options.AttemptTimeout));
        RequirePositive(options.RequestTimeout, nameof(options.RequestTimeout));
        RequirePositive(options.RateLimitWindow, nameof(options.RateLimitWindow));
        RequirePositive(options.SnapshotTimeout, nameof(options.SnapshotTimeout));

        if (options.CacheDuration < TimeSpan.Zero)
        {
            errors.Add("InventoryApi:CacheDuration must not be negative.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

        void RequirePositive(TimeSpan value, string setting)
        {
            if (value <= TimeSpan.Zero)
            {
                errors.Add($"InventoryApi:{setting} must be greater than zero.");
            }
        }
    }
}
