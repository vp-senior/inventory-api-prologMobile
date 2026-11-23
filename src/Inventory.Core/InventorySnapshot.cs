namespace Inventory.Core;

/// <summary>
/// The complete, verified set of inventory records, and when it was retrieved from the upstream system.
/// </summary>
public sealed record InventorySnapshot(IReadOnlyList<InventoryRecord> Records, DateTimeOffset RetrievedAt);
