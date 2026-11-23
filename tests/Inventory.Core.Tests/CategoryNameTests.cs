using Inventory.Core.Aggregation;

namespace Inventory.Core.Tests;

public class CategoryNameTests
{
    [Theory]
    [InlineData("Electronics", "Electronics")]
    [InlineData(" Electronics", "Electronics")]
    [InlineData("  electronics ", "Electronics")]
    [InlineData("ELECTRONICS", "Electronics")]
    [InlineData("Office Supplies ", "Office Supplies")]
    [InlineData("OFFICE SUPPLIES", "Office Supplies")]
    [InlineData("  home & kitchen ", "Home & Kitchen")]
    [InlineData("Home  &\tKitchen", "Home & Kitchen")]
    public void Every_spelling_seen_upstream_maps_to_one_canonical_name(string raw, string expected)
    {
        Assert.Equal(expected, CategoryName.Normalize(raw));
    }

    [Fact]
    public void Normalising_is_idempotent()
    {
        var once = CategoryName.Normalize("  hOmE &   kitchen ");

        Assert.Equal(once, CategoryName.Normalize(once));
    }
}
