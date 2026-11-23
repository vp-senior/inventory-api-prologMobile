using System.Diagnostics;
using Inventory.Core;
using Inventory.Core.Results;
using Inventory.Infrastructure.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Paging;

/// <summary>
/// Reads the whole dataset page by page and proves it is complete and consistent before handing
/// it on. It either returns every record or a failure that says what was wrong; it never returns
/// a partial dataset.
///
/// What "complete and consistent" means here:
///   1. every page is a valid answer to the request that was made (<see cref="PageValidator"/>);
///   2. all pages agree on the dataset's totals;
///   3. a record that appears more than once is identical every time (the copies are dropped),
///      otherwise the data contradicts itself and is rejected;
///   4. the number of distinct records equals the total the API reported;
///   5. the page after the last one is empty, so the reported total is not an understatement.
/// </summary>
internal sealed class PagedInventoryRecordSource(
    IInventoryApiClient client,
    IOptions<InventoryApiOptions> options,
    TimeProvider timeProvider,
    ILogger<PagedInventoryRecordSource> logger) : IInventoryRecordSource
{
    /// <summary>Limits the size of a failure report; the log has the same information.</summary>
    private const int MaxProblemsReported = 20;

    private readonly InventoryApiOptions _options = options.Value;

    public async Task<Result<InventorySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var pageSize = _options.PageSize;
        var collected = new CollectedRecords();

        // Page 1 tells us how big the dataset is. Every later page is checked against it.
        var firstPage = await GetVerifiedPageAsync(1, pageSize, expectedTotals: null, cancellationToken);
        if (!firstPage.IsSuccess)
        {
            return firstPage.Failure;
        }

        var totals = firstPage.Value.Totals;
        collected.Add(firstPage.Value);

        // Pages are read one at a time: the upstream rate limit (5 requests per 10 seconds) means
        // parallel requests would only queue behind the limiter and make failures harder to follow.
        // The loop runs one page past the end on purpose, to confirm that page is empty (check 5).
        for (var pageNumber = 2; pageNumber <= totals.TotalPages + 1; pageNumber++)
        {
            var page = await GetVerifiedPageAsync(pageNumber, pageSize, totals, cancellationToken);
            if (!page.IsSuccess)
            {
                return page.Failure;
            }

            collected.Add(page.Value);
        }

        var problems = collected.Conflicts.ToList();
        if (collected.Count != totals.TotalRecords)
        {
            problems.Add($"The API reports {totals.TotalRecords} records, but its {totals.TotalPages} pages contained {collected.Count} distinct records.");
        }

        if (problems.Count > 0)
        {
            logger.LogError("Inventory dataset rejected after reading all pages: {Problems}", problems);
            return new InventoryFailure(InventoryFailureKind.InvalidData, "The inventory API returned a dataset that is incomplete or contradicts itself.")
            {
                Details = problems.Take(MaxProblemsReported).ToList(),
            };
        }

        if (collected.DuplicateIds.Count > 0)
        {
            logger.LogWarning(
                "The inventory API returned {DuplicateCount} record(s) more than once; the copies were identical and have been dropped. Ids: {DuplicateIds}",
                collected.DuplicateIds.Count, collected.DuplicateIds);
        }

        logger.LogInformation(
            "Collected and verified {RecordCount} inventory records from {PageCount} pages in {ElapsedMs:F0} ms",
            collected.Count, totals.TotalPages, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return new InventorySnapshot(collected.ToOrderedList(), timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Fetches one page and validates it. A page that arrives with HTTP 200 but fails validation is
    /// requested again, up to <see cref="InventoryApiOptions.MaxPageAttempts"/> times: the dataset is
    /// static, so a correct answer on a later attempt is exactly as trustworthy as one on the first.
    /// Failed requests are not re-requested here; the HTTP pipeline has already retried those.
    /// </summary>
    private async Task<Result<VerifiedPage>> GetVerifiedPageAsync(
        int pageNumber, int pageSize, DatasetTotals? expectedTotals, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await client.GetPageAsync(pageNumber, pageSize, cancellationToken);

            InventoryFailure failure;
            if (response.IsSuccess)
            {
                var verified = PageValidator.Validate(response.Value, pageNumber, pageSize, expectedTotals, out var problems);
                if (verified is not null)
                {
                    if (pageNumber > verified.Totals.TotalPages)
                    {
                        logger.LogInformation("Inventory API page {Page}, one past the last, is empty as it should be", pageNumber);
                    }
                    else
                    {
                        logger.LogInformation(
                            "Inventory API page {Page} of {TotalPages}: {RecordCount} valid records",
                            pageNumber, verified.Totals.TotalPages, verified.Records.Count);
                    }
                    return verified;
                }

                failure = new InventoryFailure(InventoryFailureKind.InvalidData, $"Page {pageNumber} of the inventory API failed validation.")
                {
                    Details = problems,
                };
            }
            else if (response.Failure.Kind == InventoryFailureKind.InvalidData)
            {
                // HTTP 200 with a body that could not be read at all: also a content problem.
                failure = response.Failure;
            }
            else
            {
                return response.Failure;
            }

            if (attempt >= _options.MaxPageAttempts)
            {
                logger.LogError(
                    "Inventory API page {Page} was still unusable after {Attempts} attempt(s): {Message} {Problems}",
                    pageNumber, attempt, failure.Message, failure.Details);
                return failure;
            }

            logger.LogWarning(
                "Inventory API page {Page} was unusable on attempt {Attempt} of {MaxAttempts}; requesting it again. {Message} {Problems}",
                pageNumber, attempt, _options.MaxPageAttempts, failure.Message, failure.Details);
        }
    }

    /// <summary>Accumulates records across pages, reconciling any record that shows up more than once.</summary>
    private sealed class CollectedRecords
    {
        private readonly Dictionary<int, (InventoryRecord Record, int Page)> _byId = [];

        public int Count => _byId.Count;

        /// <summary>Ids that were returned more than once with identical contents.</summary>
        public SortedSet<int> DuplicateIds { get; } = [];

        /// <summary>Descriptions of ids that were returned more than once with different contents.</summary>
        public List<string> Conflicts { get; } = [];

        public void Add(VerifiedPage page)
        {
            foreach (var record in page.Records)
            {
                if (_byId.TryAdd(record.Id, (record, page.Number)))
                {
                    continue;
                }

                var (existing, firstSeenOnPage) = _byId[record.Id];
                if (existing == record)
                {
                    DuplicateIds.Add(record.Id);
                }
                else
                {
                    // Two different versions of one record: there is no sound way to pick a winner.
                    Conflicts.Add($"Record {record.Id} was returned with different contents on page {firstSeenOnPage} and page {page.Number}.");
                }
            }
        }

        public IReadOnlyList<InventoryRecord> ToOrderedList() =>
            _byId.Values.Select(entry => entry.Record).OrderBy(record => record.Id).ToList();
    }
}
