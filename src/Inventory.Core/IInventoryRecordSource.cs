using Inventory.Core.Results;

namespace Inventory.Core;

/// <summary>
/// Supplies the complete inventory dataset. The business logic depends on this abstraction only;
/// how the records are obtained (HTTP, paging, retries, caching) is the implementation's concern.
/// </summary>
public interface IInventoryRecordSource
{
    /// <summary>
    /// Returns every inventory record, or a failure describing why a complete and consistent dataset
    /// could not be obtained. Implementations never return a partial dataset as a success.
    /// </summary>
    Task<Result<InventorySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken);
}
