namespace Inventory.Core;

/// <summary>
/// One inventory record exactly as the upstream system describes it. Values are kept as received
/// (for example, <see cref="Category"/> is not normalised here) so the record stays a faithful copy of the source.
/// </summary>
public sealed record InventoryRecord(
    int Id,
    string Name,
    string Category,
    decimal Price,
    int Quantity,
    DateTimeOffset LastUpdated);
