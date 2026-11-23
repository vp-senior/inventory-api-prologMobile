namespace Inventory.Core.Aggregation;

/// <summary>The figures for one category, together with the records they were computed from.</summary>
/// <param name="Category">The canonical category name (see <see cref="CategoryName"/>).</param>
/// <param name="TotalInventoryValue">Sum of price × quantity over <paramref name="Items"/>, unrounded.</param>
/// <param name="AvailableRecordCount">Number of records in <paramref name="Items"/>.</param>
/// <param name="MostRecentlyUpdated">The record in <paramref name="Items"/> with the latest LastUpdated.</param>
/// <param name="Items">Every record that contributed to the figures above, ordered by id.</param>
public sealed record CategoryAggregate(
    string Category,
    decimal TotalInventoryValue,
    int AvailableRecordCount,
    InventoryRecord MostRecentlyUpdated,
    IReadOnlyList<InventoryRecord> Items);
