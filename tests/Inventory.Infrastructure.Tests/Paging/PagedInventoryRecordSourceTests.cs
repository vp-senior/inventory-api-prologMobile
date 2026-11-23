using Inventory.Core.Results;
using Inventory.Infrastructure.Http;
using Inventory.Infrastructure.Paging;
using Inventory.Infrastructure.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Inventory.Infrastructure.Tests.Paging;

/// <summary>
/// "A 200 is not a guarantee": each test makes the API misbehave in one specific way while still
/// answering 200, and checks the source either recovers a complete dataset or refuses to return one.
/// </summary>
public class PagedInventoryRecordSourceTests
{
    private const int PageSize = 5;

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Collects_every_record_from_every_page()
    {
        var api = new FakeInventoryApi(Wire.Dataset(12)); // 3 pages: 5 + 5 + 2

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Enumerable.Range(1, 12), result.Value.Records.Select(r => r.Id));
        Assert.Equal(_time.GetUtcNow(), result.Value.RetrievedAt);
    }

    [Fact]
    public async Task Reads_one_page_past_the_end_to_confirm_nothing_is_left()
    {
        var api = new FakeInventoryApi(Wire.Dataset(12));

        await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], api.RequestedPages);
    }

    [Fact]
    public async Task Maps_wire_values_to_domain_values()
    {
        var api = new FakeInventoryApi([Wire.Record(1001, category: "  home & kitchen ", price: 149.99m, quantity: 12, lastUpdated: "2026-10-15T12:30:00Z")]);

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        var record = Assert.Single(result.Value.Records);
        Assert.Equal(new(1001, "Item 1001", "  home & kitchen ", 149.99m, 12, new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.Zero)), record);
    }

    [Fact]
    public async Task An_empty_dataset_is_a_valid_result()
    {
        var api = new FakeInventoryApi([]);

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Records);
    }

    [Fact]
    public async Task Drops_a_record_repeated_across_a_page_boundary_when_the_copies_are_identical()
    {
        // What the real API does: page 2 starts by repeating the last record of page 1.
        var dataset = Wire.Dataset(12);
        var pageTwo = Wire.Page(dataset, 2, PageSize);
        var api = new FakeInventoryApi(dataset)
            .AlwaysOnPage(2, pageTwo with { Records = [dataset[4], .. pageTwo.Records!] });

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Enumerable.Range(1, 12), result.Value.Records.Select(r => r.Id));
    }

    [Fact]
    public async Task Rejects_the_dataset_when_a_repeated_record_differs_between_pages()
    {
        var dataset = Wire.Dataset(12);
        var pageTwo = Wire.Page(dataset, 2, PageSize);
        var changedCopy = dataset[4] with { Quantity = 999 };
        var api = new FakeInventoryApi(dataset)
            .AlwaysOnPage(2, pageTwo with { Records = [changedCopy, .. pageTwo.Records!] });

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        var failure = AssertInvalidData(result);
        Assert.Contains(failure.Details, d => d.Contains("Record 5") && d.Contains("page 1") && d.Contains("page 2"));
    }

    [Fact]
    public async Task Rejects_the_dataset_when_a_repeat_replaces_a_record_so_one_goes_missing()
    {
        // Page 2 has the right number of records, but one of them is a repeat from page 1,
        // so record 10 is never delivered. Every page looks fine on its own.
        var dataset = Wire.Dataset(12);
        var pageTwo = Wire.Page(dataset, 2, PageSize);
        var api = new FakeInventoryApi(dataset)
            .AlwaysOnPage(2, pageTwo with { Records = [dataset[4], .. pageTwo.Records!.Take(PageSize - 1)] });

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        var failure = AssertInvalidData(result);
        Assert.Contains(failure.Details, d => d.Contains("reports 12 records") && d.Contains("11 distinct"));
    }

    [Fact]
    public async Task Requests_a_short_page_again_and_succeeds_when_the_next_answer_is_complete()
    {
        var dataset = Wire.Dataset(12);
        var pageTwo = Wire.Page(dataset, 2, PageSize);
        var api = new FakeInventoryApi(dataset)
            .OnPage(2, pageTwo with { Records = pageTwo.Records!.Take(3).ToList() });

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(12, result.Value.Records.Count);
        Assert.Equal([1, 2, 2, 3, 4], api.RequestedPages);
    }

    [Fact]
    public async Task Rejects_the_dataset_when_a_page_stays_short_on_every_attempt()
    {
        var dataset = Wire.Dataset(12);
        var pageTwo = Wire.Page(dataset, 2, PageSize);
        var api = new FakeInventoryApi(dataset)
            .AlwaysOnPage(2, pageTwo with { Records = pageTwo.Records!.Take(3).ToList() });

        var result = await CreateSource(api, maxPageAttempts: 3).GetSnapshotAsync(CancellationToken.None);

        var failure = AssertInvalidData(result);
        Assert.Contains("Page 2", failure.Message);
        Assert.Contains(failure.Details, d => d.Contains("holds 3 records") && d.Contains("at least 5"));
        Assert.Equal([1, 2, 2, 2], api.RequestedPages); // three attempts, then stop: no point reading on
    }

    [Fact]
    public async Task Rejects_the_dataset_when_a_page_in_the_middle_comes_back_empty()
    {
        var dataset = Wire.Dataset(12);
        var api = new FakeInventoryApi(dataset)
            .AlwaysOnPage(2, Wire.Page(dataset, 2, PageSize) with { Records = [] });

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        AssertInvalidData(result);
    }

    [Fact]
    public async Task Rejects_the_dataset_when_the_totals_change_between_pages()
    {
        var dataset = Wire.Dataset(12);
        var api = new FakeInventoryApi(dataset)
            .AlwaysOnPage(3, Wire.Page(dataset, 3, PageSize) with { TotalRecords = 15 });

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        var failure = AssertInvalidData(result);
        Assert.Contains(failure.Details, d => d.Contains("Totals changed"));
    }

    [Fact]
    public async Task Rejects_the_dataset_when_total_pages_and_total_records_disagree()
    {
        var dataset = Wire.Dataset(12);
        var api = new FakeInventoryApi(dataset)
            .AlwaysOnPage(1, Wire.Page(dataset, 1, PageSize) with { TotalPages = 2 });

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        var failure = AssertInvalidData(result);
        Assert.Contains(failure.Details, d => d.Contains("totalPages is 2"));
        Assert.All(api.RequestedPages, page => Assert.Equal(1, page));
    }

    [Fact]
    public async Task Rejects_the_dataset_when_the_api_answers_with_a_different_page_than_requested()
    {
        var dataset = Wire.Dataset(12);
        var api = new FakeInventoryApi(dataset)
            .AlwaysOnPage(2, Wire.Page(dataset, 1, PageSize)); // page 1 served again

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        var failure = AssertInvalidData(result);
        Assert.Contains(failure.Details, d => d.Contains("Asked for page 2"));
    }

    [Fact]
    public async Task Rejects_the_dataset_when_the_api_ignores_the_requested_page_size()
    {
        var dataset = Wire.Dataset(12);
        var api = new FakeInventoryApi(dataset)
            .AlwaysOnPage(1, Wire.Page(dataset, 1, pageSize: 3));

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        var failure = AssertInvalidData(result);
        Assert.Contains(failure.Details, d => d.Contains("Asked for pageSize 5"));
    }

    [Fact]
    public async Task Rejects_the_dataset_when_there_are_records_past_the_reported_last_page()
    {
        // totalRecords and totalPages are consistent with each other and with every page,
        // but understate the dataset: only reading past the end reveals it.
        var reported = Wire.Dataset(10);
        var hidden = Wire.Record(11);
        var api = new FakeInventoryApi(reported)
            .AlwaysOnPage(3, Wire.Page(reported, 3, PageSize) with { Records = [hidden] });

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        var failure = AssertInvalidData(result);
        Assert.Contains(failure.Details, d => d.Contains("although the API reports only 2 pages"));
    }

    [Fact]
    public async Task Rejects_the_dataset_when_a_record_breaks_the_documented_contract()
    {
        var dataset = Wire.Dataset(12);
        dataset[6] = dataset[6] with { Quantity = -4 };
        var api = new FakeInventoryApi(dataset);

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        var failure = AssertInvalidData(result);
        Assert.Contains(failure.Details, d => d.Contains("Record 7") && d.Contains("quantity") && d.Contains("-4"));
    }

    [Fact]
    public async Task Requests_a_page_again_when_its_body_could_not_be_read()
    {
        var api = new FakeInventoryApi(Wire.Dataset(12))
            .OnPage(2, new InventoryFailure(InventoryFailureKind.InvalidData, "unreadable body"));

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2, 2, 3, 4], api.RequestedPages);
    }

    [Theory]
    [InlineData(InventoryFailureKind.Unavailable)]
    [InlineData(InventoryFailureKind.Timeout)]
    [InlineData(InventoryFailureKind.Rejected)]
    public async Task Stops_at_once_and_passes_the_failure_on_when_a_request_fails(InventoryFailureKind kind)
    {
        // The HTTP pipeline has already retried what can be retried; asking again here would only
        // multiply the attempts.
        var failure = new InventoryFailure(kind, "upstream said no") { RetryAfter = TimeSpan.FromSeconds(30) };
        var api = new FakeInventoryApi(Wire.Dataset(12)).AlwaysOnPage(2, failure);

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Same(failure, result.Failure);
        Assert.Equal([1, 2], api.RequestedPages);
    }

    [Fact]
    public async Task Never_returns_part_of_a_dataset_when_a_later_page_fails()
    {
        var api = new FakeInventoryApi(Wire.Dataset(12))
            .AlwaysOnPage(3, new InventoryFailure(InventoryFailureKind.Unavailable, "down"));

        var result = await CreateSource(api).GetSnapshotAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Stops_when_the_caller_cancels()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSource(new FakeInventoryApi(Wire.Dataset(12))).GetSnapshotAsync(cancelled.Token));
    }

    private PagedInventoryRecordSource CreateSource(IInventoryApiClient api, int maxPageAttempts = 3) =>
        new(
            api,
            Options.Create(new InventoryApiOptions { PageSize = PageSize, MaxPageAttempts = maxPageAttempts }),
            _time,
            NullLogger<PagedInventoryRecordSource>.Instance);

    private static InventoryFailure AssertInvalidData(Result<Core.InventorySnapshot> result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(InventoryFailureKind.InvalidData, result.Failure.Kind);
        return result.Failure;
    }
}
