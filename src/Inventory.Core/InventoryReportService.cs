using Inventory.Core.Aggregation;
using Inventory.Core.Results;

namespace Inventory.Core;

/// <summary>
/// The single use case of this application: obtain the complete dataset, then aggregate it.
/// </summary>
public sealed class InventoryReportService(IInventoryRecordSource source, InventoryAggregator aggregator)
{
    public async Task<Result<InventoryReport>> GetReportAsync(CancellationToken cancellationToken)
    {
        var snapshot = await source.GetSnapshotAsync(cancellationToken);
        if (!snapshot.IsSuccess)
        {
            return snapshot.Failure;
        }

        var aggregation = aggregator.Aggregate(snapshot.Value.Records);
        return new InventoryReport(aggregation, snapshot.Value.RetrievedAt);
    }
}
