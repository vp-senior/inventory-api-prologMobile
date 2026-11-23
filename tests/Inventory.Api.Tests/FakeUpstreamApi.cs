using System.Net;
using System.Text;
using System.Text.Json;

namespace Inventory.Api.Tests;

/// <summary>
/// A stand-in for the upstream Inventory API that reproduces what the real one was observed to do:
/// it requires the API key, pages the dataset, repeats the previous page's last record at the
/// top of every even page, and returns an empty page past the end. Faults can be injected.
/// </summary>
internal sealed class FakeUpstreamApi : HttpMessageHandler
{
    public const string ApiKey = "test-api-key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<UpstreamRecord> _dataset;
    private int _requestCount;

    public FakeUpstreamApi(IReadOnlyList<UpstreamRecord> dataset) => _dataset = dataset;

    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>
    /// Called before each request is served, with the 1-based request number. Return a response
    /// to send it instead of the normal page, throw to simulate a transport failure, or return
    /// null to behave normally.
    /// </summary>
    public Func<int, HttpResponseMessage?>? Fault { get; set; }

    /// <summary>Lets a test alter a page's records just before it is sent (page number, records).</summary>
    public Func<int, List<UpstreamRecord>, List<UpstreamRecord>>? Tamper { get; set; }

    public static UpstreamRecord Record(int id, string category, decimal price, int quantity, string lastUpdated) =>
        new(id, $"Item {id}", category, price, quantity, lastUpdated);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestNumber = Interlocked.Increment(ref _requestCount);

        if (Fault?.Invoke(requestNumber) is { } faultResponse)
        {
            return Task.FromResult(faultResponse);
        }

        if (!request.Headers.TryGetValues("x-api-key", out var keys) || keys.Single() != ApiKey)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var page = int.Parse(query["page"]!, System.Globalization.CultureInfo.InvariantCulture);
        var pageSize = int.Parse(query["pageSize"]!, System.Globalization.CultureInfo.InvariantCulture);

        var records = _dataset.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        if (page % 2 == 0 && records.Count > 0)
        {
            records.Insert(0, _dataset[((page - 1) * pageSize) - 1]);
        }

        records = Tamper?.Invoke(page, records) ?? records;

        var body = JsonSerializer.Serialize(
            new
            {
                records,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling(_dataset.Count / (double)pageSize),
                totalRecords = _dataset.Count,
            },
            JsonOptions);

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed record UpstreamRecord(int Id, string Name, string Category, decimal Price, int Quantity, string LastUpdated);
