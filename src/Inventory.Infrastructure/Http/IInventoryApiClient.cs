using Inventory.Core.Results;

namespace Inventory.Infrastructure.Http;

/// <summary>Fetches a single page from the upstream Inventory API.</summary>
internal interface IInventoryApiClient
{
    /// <summary>
    /// Returns the page as received, or a failure. Non-success status codes, transport errors, timeouts
    /// and unreadable bodies all come back as failures; the only exception that escapes is
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<Result<RecordsPageDto>> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken);
}
