using Inventory.Core.Aggregation;
using Inventory.Core.Results;

namespace Inventory.Core.Tests;

public class InventoryReportServiceTests
{
    [Fact]
    public async Task Aggregates_the_snapshot_and_keeps_its_retrieval_time()
    {
        var retrievedAt = new DateTimeOffset(2026, 11, 23, 9, 30, 0, TimeSpan.Zero);
        var source = new StubSource(new InventorySnapshot(
            [Records.Create(1, quantity: 2, price: 3m), Records.Create(2, quantity: 0)],
            retrievedAt));
        var service = new InventoryReportService(source, new InventoryAggregator());

        var result = await service.GetReportAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(retrievedAt, result.Value.RetrievedAt);
        Assert.Equal(2, result.Value.Aggregation.TotalRecordCount);
        Assert.Equal(6m, Assert.Single(result.Value.Aggregation.Categories).TotalInventoryValue);
    }

    [Fact]
    public async Task Passes_a_source_failure_through_untouched()
    {
        var failure = new InventoryFailure(InventoryFailureKind.Unavailable, "down") { RetryAfter = TimeSpan.FromSeconds(9) };
        var service = new InventoryReportService(new StubSource(failure), new InventoryAggregator());

        var result = await service.GetReportAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Same(failure, result.Failure);
    }

    private sealed class StubSource(Result<InventorySnapshot> result) : IInventoryRecordSource
    {
        public Task<Result<InventorySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
