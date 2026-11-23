using Inventory.Core;
using Inventory.Core.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Caching;

/// <summary>
/// Wraps another source so that the expensive upstream collection (about 10 seconds, because of
/// the upstream rate limit) happens at most once at a time and its result is reused for a while.
///
/// - A verified dataset is reused for <see cref="InventoryApiOptions.CacheDuration"/>. Failures are never cached.
/// - Callers that arrive while a collection is running share that one run instead of each
///   starting their own, which would only make them compete for the same rate limit.
/// </summary>
internal sealed class CachedInventoryRecordSource(
    IInventoryRecordSource inner,
    IOptions<InventoryApiOptions> options,
    TimeProvider timeProvider,
    ILogger<CachedInventoryRecordSource> logger) : IInventoryRecordSource
{
    private readonly InventoryApiOptions _options = options.Value;
    private readonly Lock _lock = new();

    private InventorySnapshot? _cached;
    private DateTimeOffset _cachedUntil;
    private Task<Result<InventorySnapshot>>? _refresh;

    public Task<Result<InventorySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        Task<Result<InventorySnapshot>> refresh;
        lock (_lock)
        {
            if (_cached is not null && timeProvider.GetUtcNow() < _cachedUntil)
            {
                return Task.FromResult(Result<InventorySnapshot>.Success(_cached));
            }

            refresh = _refresh ??= RefreshAsync();
        }

        // The shared run is not tied to any one caller: a caller that gives up stops waiting,
        // but the run carries on for the others (and for the cache).
        return refresh.WaitAsync(cancellationToken);
    }

    private async Task<Result<InventorySnapshot>> RefreshAsync()
    {
        // Return to the caller (which is holding the lock) before doing any work.
        await Task.Yield();

        try
        {
            using var timeout = new CancellationTokenSource(_options.SnapshotTimeout, timeProvider);

            Result<InventorySnapshot> result;
            try
            {
                result = await inner.GetSnapshotAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                logger.LogError("Collecting the inventory dataset did not finish within {Timeout}", _options.SnapshotTimeout);
                return new InventoryFailure(InventoryFailureKind.Timeout, "The inventory data could not be collected from the inventory API in time.");
            }

            if (result.IsSuccess && _options.CacheDuration > TimeSpan.Zero)
            {
                lock (_lock)
                {
                    _cached = result.Value;
                    _cachedUntil = timeProvider.GetUtcNow() + _options.CacheDuration;
                }
            }

            return result;
        }
        finally
        {
            lock (_lock)
            {
                _refresh = null;
            }
        }
    }
}
