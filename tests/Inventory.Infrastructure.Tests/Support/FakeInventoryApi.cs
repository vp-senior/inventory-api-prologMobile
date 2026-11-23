using Inventory.Core.Results;
using Inventory.Infrastructure.Http;

namespace Inventory.Infrastructure.Tests.Support;

/// <summary>
/// An in-memory Inventory API for testing the paging logic without HTTP. By default it serves a
/// dataset the way a well-behaved API would; individual pages can be overridden to misbehave.
/// </summary>
internal sealed class FakeInventoryApi(IReadOnlyList<RecordDto> dataset) : IInventoryApiClient
{
    private readonly Dictionary<int, Queue<Result<RecordsPageDto>>> _overrides = [];

    /// <summary>Every page number requested, in order.</summary>
    public List<int> RequestedPages { get; } = [];

    /// <summary>
    /// Answers the next requests for <paramref name="page"/> with these responses, one each.
    /// After they are used up the page is served normally again.
    /// </summary>
    public FakeInventoryApi OnPage(int page, params Result<RecordsPageDto>[] responses)
    {
        _overrides[page] = new Queue<Result<RecordsPageDto>>(responses);
        return this;
    }

    /// <summary>Answers every request for <paramref name="page"/> with the same response.</summary>
    public FakeInventoryApi AlwaysOnPage(int page, Result<RecordsPageDto> response) =>
        OnPage(page, Enumerable.Repeat(response, 100).ToArray());

    public Task<Result<RecordsPageDto>> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedPages.Add(page);

        var response = _overrides.TryGetValue(page, out var queued) && queued.Count > 0
            ? queued.Dequeue()
            : Wire.Page(dataset, page, pageSize);

        return Task.FromResult(response);
    }
}
