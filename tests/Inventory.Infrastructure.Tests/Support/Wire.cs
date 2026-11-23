using System.Text.Json;
using Inventory.Infrastructure.Http;

namespace Inventory.Infrastructure.Tests.Support;

/// <summary>Builds wire-format records, pages and JSON bodies for tests.</summary>
internal static class Wire
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RecordDto Record(
        int id,
        string category = "Electronics",
        decimal price = 10.00m,
        int quantity = 1,
        string lastUpdated = "2026-09-01T00:00:00Z") =>
        new(id, $"Item {id}", category, price, quantity, lastUpdated);

    /// <summary>Records with ids 1..count.</summary>
    public static List<RecordDto> Dataset(int count) => Enumerable.Range(1, count).Select(id => Record(id)).ToList();

    /// <summary>The page a well-behaved API would return for this dataset.</summary>
    public static RecordsPageDto Page(IReadOnlyList<RecordDto> dataset, int page, int pageSize) =>
        new(
            dataset.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            page,
            pageSize,
            TotalPages: (int)Math.Ceiling(dataset.Count / (double)pageSize),
            TotalRecords: dataset.Count);

    public static string Json(RecordsPageDto page) => JsonSerializer.Serialize(page, JsonOptions);
}
