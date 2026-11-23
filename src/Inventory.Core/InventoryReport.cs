using Inventory.Core.Aggregation;

namespace Inventory.Core;

/// <summary>The aggregated inventory, and when the data behind it was retrieved from the source.</summary>
public sealed record InventoryReport(InventoryAggregation Aggregation, DateTimeOffset RetrievedAt);
