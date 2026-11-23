namespace Inventory.Core.Aggregation;

/// <summary>
/// The business rules of the assignment as a pure function: no I/O, no clock, no HTTP.
/// The same input always produces the same output, in the same order.
/// </summary>
public sealed class InventoryAggregator
{
    public InventoryAggregation Aggregate(IReadOnlyCollection<InventoryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        // Rule 1: records without stock contribute to nothing below.
        var available = records.Where(IsAvailableStock).ToList();

        // Rule 2: group what remains by category. Categories are ordered by name for a stable response.
        var categories = available
            .GroupBy(record => CategoryName.Normalize(record.Category), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => AggregateCategory(group.Key, group))
            .ToList();

        return new InventoryAggregation(categories, records.Count, records.Count - available.Count);
    }

    private static bool IsAvailableStock(InventoryRecord record) => record.Quantity > 0;

    private static CategoryAggregate AggregateCategory(string category, IEnumerable<InventoryRecord> records)
    {
        // Rule 6: the records the figures are computed from, in a stable order.
        var items = records.OrderBy(record => record.Id).ToList();

        return new CategoryAggregate(
            category,
            // Rule 3: decimal arithmetic, so money is exact; rounding is a presentation decision.
            TotalInventoryValue: items.Sum(record => record.Price * record.Quantity),
            // Rule 4.
            AvailableRecordCount: items.Count,
            // Rule 5: latest LastUpdated wins; if several records share it, the lowest id wins.
            MostRecentlyUpdated: items
                .OrderByDescending(record => record.LastUpdated)
                .ThenBy(record => record.Id)
                .First(),
            items);
    }
}
