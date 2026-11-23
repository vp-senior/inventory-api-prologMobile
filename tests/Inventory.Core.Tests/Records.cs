namespace Inventory.Core.Tests;

/// <summary>Builds records for tests, so each test states only the fields it cares about.</summary>
internal static class Records
{
    public static readonly DateTimeOffset BaseTime = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    public static InventoryRecord Create(
        int id,
        string category = "Electronics",
        decimal price = 10.00m,
        int quantity = 1,
        DateTimeOffset? lastUpdated = null,
        string? name = null) =>
        new(id, name ?? $"Item {id}", category, price, quantity, lastUpdated ?? BaseTime);
}
