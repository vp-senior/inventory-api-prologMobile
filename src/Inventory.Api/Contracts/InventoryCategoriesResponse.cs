using Inventory.Core;
using Inventory.Core.Aggregation;

namespace Inventory.Api.Contracts;

/// <summary>
/// The response of GET /api/inventory/categories. These types are the public JSON contract and are
/// kept separate from the domain types so that either can change without silently changing the other.
/// </summary>
public sealed record InventoryCategoriesResponse(SummaryResponse Summary, IReadOnlyList<CategoryResponse> Categories)
{
    public static InventoryCategoriesResponse From(InventoryReport report)
    {
        var aggregation = report.Aggregation;

        return new InventoryCategoriesResponse(
            new SummaryResponse(
                RetrievedAt: report.RetrievedAt.UtcDateTime,
                TotalRecords: aggregation.TotalRecordCount,
                AvailableRecords: aggregation.AvailableRecordCount,
                ExcludedRecords: aggregation.ExcludedRecordCount,
                CategoryCount: aggregation.Categories.Count),
            aggregation.Categories.Select(CategoryResponse.From).ToList());
    }
}

/// <summary>Figures that let a caller reconcile the categories against the source dataset.</summary>
/// <param name="RetrievedAt">When the underlying data was read from the Inventory API (UTC). Results are cached briefly.</param>
/// <param name="TotalRecords">Records in the source dataset.</param>
/// <param name="AvailableRecords">Records with stock on hand; the sum of every category's availableRecordCount.</param>
/// <param name="ExcludedRecords">Records left out because their quantity is zero.</param>
/// <param name="CategoryCount">Number of categories returned.</param>
public sealed record SummaryResponse(
    DateTime RetrievedAt,
    int TotalRecords,
    int AvailableRecords,
    int ExcludedRecords,
    int CategoryCount);

public sealed record CategoryResponse(
    string Category,
    decimal TotalInventoryValue,
    int AvailableRecordCount,
    MostRecentlyUpdatedResponse MostRecentlyUpdated,
    IReadOnlyList<ItemResponse> Items)
{
    public static CategoryResponse From(CategoryAggregate category) =>
        new(
            category.Category,
            // Money is summed exactly and rounded to cents only here, where it is presented.
            decimal.Round(category.TotalInventoryValue, 2, MidpointRounding.AwayFromZero),
            category.AvailableRecordCount,
            new MostRecentlyUpdatedResponse(
                category.MostRecentlyUpdated.Id,
                category.MostRecentlyUpdated.Name,
                category.MostRecentlyUpdated.LastUpdated.UtcDateTime),
            category.Items.Select(ItemResponse.From).ToList());
}

public sealed record MostRecentlyUpdatedResponse(int Id, string Name, DateTime LastUpdated);

public sealed record ItemResponse(int Id, string Name, decimal Price, int Quantity, DateTime LastUpdated)
{
    // UtcDateTime has Kind=Utc, which serialises with a trailing "Z" like the upstream format.
    public static ItemResponse From(InventoryRecord record) =>
        new(record.Id, record.Name, record.Price, record.Quantity, record.LastUpdated.UtcDateTime);
}
