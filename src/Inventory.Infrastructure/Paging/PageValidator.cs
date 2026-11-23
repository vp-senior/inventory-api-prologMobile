using Inventory.Core;
using Inventory.Infrastructure.Http;

namespace Inventory.Infrastructure.Paging;

/// <summary>The dataset size the API reported; every page must report the same numbers.</summary>
internal readonly record struct DatasetTotals(int TotalPages, int TotalRecords);

/// <summary>A page whose envelope and records all passed validation.</summary>
internal sealed record VerifiedPage(int Number, DatasetTotals Totals, IReadOnlyList<InventoryRecord> Records);

/// <summary>
/// Decides whether an HTTP 200 page can be trusted. Pure: given the same page it always gives the
/// same verdict, which keeps every rule below individually testable.
/// </summary>
internal static class PageValidator
{
    /// <summary>Limits how many problems one page can report, so a wholly corrupt page stays readable.</summary>
    private const int MaxProblemsReported = 10;

    /// <param name="page">The response body to check.</param>
    /// <param name="requestedPage">The page number that was asked for.</param>
    /// <param name="requestedPageSize">The page size that was asked for.</param>
    /// <param name="expectedTotals">Totals reported by page 1; null while validating page 1 itself.</param>
    /// <param name="problems">Empty when the page is valid; otherwise what is wrong with it.</param>
    public static VerifiedPage? Validate(
        RecordsPageDto page,
        int requestedPage,
        int requestedPageSize,
        DatasetTotals? expectedTotals,
        out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        problems = found;

        // The response must be an answer to the question that was asked.
        if (page.Page != requestedPage)
        {
            found.Add($"Asked for page {requestedPage} but the response says it is page {Describe(page.Page)}.");
        }

        if (page.PageSize != requestedPageSize)
        {
            found.Add($"Asked for pageSize {requestedPageSize} but the response says pageSize {Describe(page.PageSize)}.");
        }

        if (page.TotalRecords is not >= 0 || page.TotalPages is not >= 0)
        {
            found.Add($"totalRecords ({Describe(page.TotalRecords)}) and totalPages ({Describe(page.TotalPages)}) must both be present and not negative.");
        }

        if (page.Records is null)
        {
            found.Add("The response has no 'records' array.");
        }

        if (found.Count > 0)
        {
            // Without a sound envelope the remaining checks would only produce noise.
            return null;
        }

        var totals = new DatasetTotals(page.TotalPages!.Value, page.TotalRecords!.Value);

        // The two totals must agree with each other...
        var impliedPages = (int)Math.Ceiling(totals.TotalRecords / (double)requestedPageSize);
        if (totals.TotalPages != impliedPages)
        {
            found.Add($"totalPages is {totals.TotalPages}, but {totals.TotalRecords} records at {requestedPageSize} per page is {impliedPages} pages.");
        }

        // ...and with what the first page said, since the dataset is static.
        if (expectedTotals is { } expected && totals != expected)
        {
            found.Add($"Totals changed between pages: page 1 reported {expected.TotalRecords} records in {expected.TotalPages} pages, this page reports {totals.TotalRecords} in {totals.TotalPages}.");
        }

        // A page must hold at least the records that belong on it. More than that is tolerated here:
        // the real API repeats the previous page's last record on some pages, and repeats are
        // reconciled (or rejected) across pages by the caller. Fewer means records are missing.
        var records = page.Records!;
        var minimumRecords = Math.Clamp(totals.TotalRecords - ((long)(requestedPage - 1) * requestedPageSize), 0, requestedPageSize);
        if (records.Count < minimumRecords)
        {
            found.Add($"The page holds {records.Count} records but should hold at least {minimumRecords}.");
        }

        // Past the last page there must be nothing; otherwise totalPages understates the dataset.
        if (requestedPage > totals.TotalPages && records.Count > 0)
        {
            found.Add($"The page holds {records.Count} records although the API reports only {totals.TotalPages} pages.");
        }

        var mapped = new List<InventoryRecord>(records.Count);
        for (var index = 0; index < records.Count && found.Count < MaxProblemsReported; index++)
        {
            if (InventoryRecordMapper.Map(records[index], position: index + 1, found) is { } record)
            {
                mapped.Add(record);
            }
        }

        return found.Count == 0 ? new VerifiedPage(requestedPage, totals, mapped) : null;
    }

    private static string Describe(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing";
}
