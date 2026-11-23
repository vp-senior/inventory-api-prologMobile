namespace Inventory.Core.Aggregation;

/// <summary>The per-category results, plus the counts needed to reconcile them against the source data.</summary>
/// <param name="Categories">One entry per category that has available stock, ordered by name.</param>
/// <param name="TotalRecordCount">How many records were aggregated, before any exclusion.</param>
/// <param name="ExcludedRecordCount">How many of those were left out because they have no stock on hand.</param>
public sealed record InventoryAggregation(
    IReadOnlyList<CategoryAggregate> Categories,
    int TotalRecordCount,
    int ExcludedRecordCount)
{
    public int AvailableRecordCount => TotalRecordCount - ExcludedRecordCount;
}
