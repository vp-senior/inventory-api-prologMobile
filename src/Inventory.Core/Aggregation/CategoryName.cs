using System.Globalization;

namespace Inventory.Core.Aggregation;

/// <summary>
/// The upstream data spells the same category in many ways (" Home &amp; Kitchen", "home &amp; kitchen ",
/// "HOME &amp; KITCHEN"). This turns every spelling into one canonical name so they group together.
/// </summary>
public static class CategoryName
{
    /// <summary>
    /// Trims, collapses runs of whitespace to a single space, and applies invariant title case:
    /// "  home &amp;  KITCHEN " becomes "Home &amp; Kitchen".
    /// </summary>
    public static string Normalize(string rawCategory)
    {
        ArgumentNullException.ThrowIfNull(rawCategory);

        var words = rawCategory.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var singleSpaced = string.Join(' ', words);

        // ToTitleCase leaves all-caps words alone (it treats them as acronyms), so lower-case first.
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(singleSpaced.ToLowerInvariant());
    }
}
