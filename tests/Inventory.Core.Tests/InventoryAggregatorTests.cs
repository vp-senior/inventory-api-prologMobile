using Inventory.Core.Aggregation;

namespace Inventory.Core.Tests;

public class InventoryAggregatorTests
{
    private readonly InventoryAggregator _aggregator = new();

    [Fact]
    public void Empty_input_produces_no_categories()
    {
        var result = _aggregator.Aggregate([]);

        Assert.Empty(result.Categories);
        Assert.Equal(0, result.TotalRecordCount);
        Assert.Equal(0, result.ExcludedRecordCount);
    }

    [Fact]
    public void Zero_quantity_records_contribute_to_no_figure()
    {
        var newestButOutOfStock = Records.Create(2, price: 999.99m, quantity: 0, lastUpdated: Records.BaseTime.AddDays(30));
        var inStock = Records.Create(1, price: 5.00m, quantity: 3, lastUpdated: Records.BaseTime);

        var category = Assert.Single(_aggregator.Aggregate([inStock, newestButOutOfStock]).Categories);

        Assert.Equal(15.00m, category.TotalInventoryValue);
        Assert.Equal(1, category.AvailableRecordCount);
        Assert.Equal(1, category.MostRecentlyUpdated.Id);
        Assert.Equal([inStock], category.Items);
    }

    [Fact]
    public void A_category_with_only_zero_quantity_records_is_omitted()
    {
        var result = _aggregator.Aggregate(
        [
            Records.Create(1, category: "Discontinued", quantity: 0),
            Records.Create(2, category: "Discontinued", quantity: 0),
            Records.Create(3, category: "Toys", quantity: 4),
        ]);

        var category = Assert.Single(result.Categories);
        Assert.Equal("Toys", category.Category);
    }

    [Fact]
    public void Counts_reconcile_with_the_input()
    {
        var result = _aggregator.Aggregate(
        [
            Records.Create(1, quantity: 0),
            Records.Create(2, quantity: 2),
            Records.Create(3, category: "Toys", quantity: 0),
            Records.Create(4, category: "Toys", quantity: 7),
            Records.Create(5, category: "Toys", quantity: 1),
        ]);

        Assert.Equal(5, result.TotalRecordCount);
        Assert.Equal(2, result.ExcludedRecordCount);
        Assert.Equal(3, result.AvailableRecordCount);
        Assert.Equal(result.AvailableRecordCount, result.Categories.Sum(c => c.AvailableRecordCount));
    }

    [Fact]
    public void Total_value_is_the_sum_of_price_times_quantity()
    {
        var category = Assert.Single(_aggregator.Aggregate(
        [
            Records.Create(1, price: 149.99m, quantity: 12),
            Records.Create(2, price: 0.10m, quantity: 3),
            Records.Create(3, price: 19.95m, quantity: 1),
        ]).Categories);

        Assert.Equal(1799.88m + 0.30m + 19.95m, category.TotalInventoryValue);
    }

    [Fact]
    public void Total_value_is_exact_where_binary_floating_point_would_drift()
    {
        // 0.1 + 0.2 != 0.3 in double arithmetic; money must not behave like that.
        var category = Assert.Single(_aggregator.Aggregate(
        [
            Records.Create(1, price: 0.1m, quantity: 1),
            Records.Create(2, price: 0.2m, quantity: 1),
        ]).Categories);

        Assert.Equal(0.3m, category.TotalInventoryValue);
    }

    [Fact]
    public void A_free_item_in_stock_still_counts_as_available()
    {
        var category = Assert.Single(_aggregator.Aggregate([Records.Create(1, price: 0m, quantity: 5)]).Categories);

        Assert.Equal(0m, category.TotalInventoryValue);
        Assert.Equal(1, category.AvailableRecordCount);
    }

    [Fact]
    public void Most_recently_updated_is_the_record_with_the_latest_timestamp()
    {
        var category = Assert.Single(_aggregator.Aggregate(
        [
            Records.Create(1, lastUpdated: Records.BaseTime.AddDays(1)),
            Records.Create(2, lastUpdated: Records.BaseTime.AddDays(3)),
            Records.Create(3, lastUpdated: Records.BaseTime.AddDays(2)),
        ]).Categories);

        Assert.Equal(2, category.MostRecentlyUpdated.Id);
    }

    [Fact]
    public void Most_recently_updated_tie_goes_to_the_lowest_id_regardless_of_input_order()
    {
        var latest = Records.BaseTime.AddDays(5);
        var records = new[]
        {
            Records.Create(7, lastUpdated: latest),
            Records.Create(3, lastUpdated: latest),
            Records.Create(9, lastUpdated: latest),
            Records.Create(1, lastUpdated: Records.BaseTime),
        };

        var forwards = Assert.Single(_aggregator.Aggregate(records).Categories);
        var backwards = Assert.Single(_aggregator.Aggregate(records.Reverse().ToArray()).Categories);

        Assert.Equal(3, forwards.MostRecentlyUpdated.Id);
        Assert.Equal(3, backwards.MostRecentlyUpdated.Id);
    }

    [Fact]
    public void Most_recently_updated_compares_instants_not_clock_text()
    {
        // 12:00 at +05:00 is 07:00 UTC, which is earlier than 08:00 UTC even though "12" looks later.
        var earlierInstant = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(5));
        var laterInstant = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        var category = Assert.Single(_aggregator.Aggregate(
        [
            Records.Create(1, lastUpdated: earlierInstant),
            Records.Create(2, lastUpdated: laterInstant),
        ]).Categories);

        Assert.Equal(2, category.MostRecentlyUpdated.Id);
    }

    [Fact]
    public void Differently_spelled_categories_are_grouped_under_one_canonical_name()
    {
        var result = _aggregator.Aggregate(
        [
            Records.Create(1, category: "Home & Kitchen", price: 1m),
            Records.Create(2, category: "  home & kitchen ", price: 2m),
            Records.Create(3, category: "HOME & KITCHEN", price: 4m),
            Records.Create(4, category: " Home  &  Kitchen", price: 8m),
        ]);

        var category = Assert.Single(result.Categories);
        Assert.Equal("Home & Kitchen", category.Category);
        Assert.Equal(4, category.AvailableRecordCount);
        Assert.Equal(15m, category.TotalInventoryValue);
    }

    [Fact]
    public void Items_are_exactly_the_records_the_figures_were_computed_from_ordered_by_id()
    {
        var result = _aggregator.Aggregate(
        [
            Records.Create(30, category: "Toys", price: 2.50m, quantity: 2),
            Records.Create(10, category: "Toys", price: 1.25m, quantity: 4),
            Records.Create(20, category: "Toys", quantity: 0),
            Records.Create(40, category: "Outdoor", price: 100m, quantity: 1),
        ]);

        var toys = result.Categories.Single(c => c.Category == "Toys");
        Assert.Equal([10, 30], toys.Items.Select(i => i.Id));
        Assert.Equal(toys.Items.Count, toys.AvailableRecordCount);
        Assert.Equal(toys.Items.Sum(i => i.Price * i.Quantity), toys.TotalInventoryValue);
        Assert.Contains(toys.MostRecentlyUpdated, toys.Items);
    }

    [Fact]
    public void Categories_are_ordered_by_name()
    {
        var result = _aggregator.Aggregate(
        [
            Records.Create(1, category: "toys"),
            Records.Create(2, category: "Electronics"),
            Records.Create(3, category: "OUTDOOR"),
        ]);

        Assert.Equal(["Electronics", "Outdoor", "Toys"], result.Categories.Select(c => c.Category));
    }

    [Fact]
    public void Aggregation_does_not_depend_on_input_order()
    {
        var records = Enumerable.Range(1, 40)
            .Select(i => Records.Create(
                i,
                category: (i % 3) switch { 0 => "Toys", 1 => " toys ", _ => "Outdoor" },
                price: i * 1.01m,
                quantity: i % 5,
                lastUpdated: Records.BaseTime.AddHours(i * 7 % 13)))
            .ToArray();

        var forwards = _aggregator.Aggregate(records);
        var backwards = _aggregator.Aggregate(records.Reverse().ToArray());

        Assert.Equal(forwards.Categories.Count, backwards.Categories.Count);
        foreach (var (a, b) in forwards.Categories.Zip(backwards.Categories))
        {
            Assert.Equal(a.Category, b.Category);
            Assert.Equal(a.TotalInventoryValue, b.TotalInventoryValue);
            Assert.Equal(a.MostRecentlyUpdated, b.MostRecentlyUpdated);
            Assert.Equal(a.Items, b.Items);
        }
    }
}
