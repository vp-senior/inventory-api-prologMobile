using System.Net;
using Inventory.Core.Results;
using Inventory.Infrastructure.Tests.Support;

namespace Inventory.Infrastructure.Tests.Http;

/// <summary>
/// The client and its resilience pipeline, exercised together through the production DI registration.
/// </summary>
public class InventoryApiClientTests
{
    private static readonly string OnePageBody = Wire.Json(Wire.Page(Wire.Dataset(3), page: 1, pageSize: 25));

    [Fact]
    public async Task Sends_the_documented_request_with_the_api_key_header()
    {
        await using var harness = new ClientHarness();
        harness.Http.RespondJson(OnePageBody);

        await harness.Client.GetPageAsync(page: 3, pageSize: 25, CancellationToken.None);

        var request = Assert.Single(harness.Http.Requests);
        Assert.Equal("https://inventory.test/api/records?page=3&pageSize=25", request.Uri.AbsoluteUri);
        Assert.Equal(ClientHarness.ApiKey, request.Headers["x-api-key"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
    }

    [Fact]
    public async Task Returns_the_page_exactly_as_the_api_sent_it()
    {
        await using var harness = new ClientHarness();
        harness.Http.RespondJson("""
            {
              "records": [
                { "id": 1001, "name": "Thermal Receipt Printer", "category": "Electronics", "price": 149.99, "quantity": 12, "lastUpdated": "2026-10-15T12:30:00Z" }
              ],
              "page": 1, "pageSize": 20, "totalPages": 8, "totalRecords": 157
            }
            """);

        var result = await harness.Client.GetPageAsync(1, 20, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal((1, 20, 8, 157), (result.Value.Page, result.Value.PageSize, result.Value.TotalPages, result.Value.TotalRecords));
        var record = Assert.Single(result.Value.Records!);
        Assert.Equal(new(1001, "Thermal Receipt Printer", "Electronics", 149.99m, 12, "2026-10-15T12:30:00Z"), record);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)599)]
    public async Task Retries_a_transient_error_status_and_succeeds_when_the_api_recovers(HttpStatusCode transientStatus)
    {
        await using var harness = new ClientHarness();
        harness.Http.Respond(transientStatus).Respond(transientStatus).RespondJson(OnePageBody);

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Retries_a_transport_failure_and_succeeds_when_the_connection_recovers()
    {
        await using var harness = new ClientHarness();
        harness.Http.Throw(new HttpRequestException("connection refused")).RespondJson(OnePageBody);

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Abandons_an_attempt_that_hangs_and_retries_it()
    {
        await using var harness = new ClientHarness();
        harness.Http.Hang().RespondJson(OnePageBody);

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.True(result.IsSuccess);
        var requests = harness.Http.Requests;
        Assert.Equal(2, requests.Count);
        Assert.True(requests[1].SentAt - requests[0].SentAt >= TimeSpan.FromSeconds(2), "The second attempt must not start before the attempt timeout.");
    }

    [Fact]
    public async Task Gives_up_after_the_configured_retries_and_reports_unavailable_without_throwing()
    {
        await using var harness = new ClientHarness(s => s["InventoryApi:MaxRetryAttempts"] = "2");
        harness.Http.Always(HttpStatusCode.InternalServerError, """{"title":"Internal Server Error"}""");

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.False(result.IsSuccess);
        Assert.Equal(InventoryFailureKind.Unavailable, result.Failure.Kind);
        Assert.Contains("500", result.Failure.Message);
        Assert.Equal(3, harness.Http.Requests.Count); // the first attempt plus two retries
    }

    [Fact]
    public async Task Reports_unavailable_without_throwing_when_the_api_cannot_be_reached_at_all()
    {
        await using var harness = new ClientHarness();
        harness.Http.AlwaysThrow(new HttpRequestException("no such host"));

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.False(result.IsSuccess);
        Assert.Equal(InventoryFailureKind.Unavailable, result.Failure.Kind);
        Assert.Equal(4, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Reports_a_timeout_without_throwing_when_every_attempt_hangs()
    {
        await using var harness = new ClientHarness();
        harness.Http.AlwaysHang();

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.False(result.IsSuccess);
        Assert.Equal(InventoryFailureKind.Timeout, result.Failure.Kind);
        Assert.Equal(4, harness.Http.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData((HttpStatusCode)418)]
    public async Task Does_not_retry_an_error_that_would_only_repeat(HttpStatusCode status)
    {
        await using var harness = new ClientHarness();
        harness.Http.Always(status, """{"detail":"page must be an integer of 1 or greater."}""");

        var result = await harness.RunAsync(harness.Client.GetPageAsync(0, 25, CancellationToken.None));

        Assert.False(result.IsSuccess);
        Assert.Equal(InventoryFailureKind.Rejected, result.Failure.Kind);
        Assert.Single(harness.Http.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task Treats_an_unexpected_non_error_status_as_a_failure_rather_than_as_data(HttpStatusCode status)
    {
        await using var harness = new ClientHarness();
        harness.Http.Always(status);

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.False(result.IsSuccess);
        Assert.Equal(InventoryFailureKind.Rejected, result.Failure.Kind);
        Assert.Single(harness.Http.Requests);
    }

    [Fact]
    public async Task Explains_rejected_credentials_as_a_configuration_problem()
    {
        await using var harness = new ClientHarness();
        harness.Http.Always(HttpStatusCode.Unauthorized);

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.False(result.IsSuccess);
        Assert.Contains("API key", result.Failure.Message);
        Assert.DoesNotContain(ClientHarness.ApiKey, result.Failure.Message);
    }

    [Fact]
    public async Task Waits_for_the_time_named_in_retry_after_before_retrying_a_429()
    {
        await using var harness = new ClientHarness();
        harness.Http
            .Respond(HttpStatusCode.TooManyRequests, configure: r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(9)))
            .RespondJson(OnePageBody);

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.True(result.IsSuccess);
        var requests = harness.Http.Requests;
        Assert.Equal(2, requests.Count);

        // Ordinary backoff here would be about 0.2s; only honouring the header explains a 9s wait.
        Assert.True(requests[1].SentAt - requests[0].SentAt >= TimeSpan.FromSeconds(9), "Retried before Retry-After had elapsed.");
    }

    [Fact]
    public async Task Understands_retry_after_given_as_a_date()
    {
        await using var harness = new ClientHarness();
        var retryAt = harness.Time.GetUtcNow().AddSeconds(6);
        harness.Http
            .Respond(HttpStatusCode.ServiceUnavailable, configure: r => r.Headers.RetryAfter = new(retryAt))
            .RespondJson(OnePageBody);

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.True(result.IsSuccess);
        Assert.True(harness.Http.Requests[1].SentAt >= retryAt, "Retried before the Retry-After date.");
    }

    [Fact]
    public async Task Ignores_a_retry_after_of_zero_and_retries_with_ordinary_backoff()
    {
        await using var harness = new ClientHarness();
        harness.Http
            .Respond(HttpStatusCode.TooManyRequests, configure: r => r.Headers.RetryAfter = new(TimeSpan.Zero))
            .RespondJson(OnePageBody);

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Fails_fast_and_passes_the_wait_on_when_retry_after_is_longer_than_we_will_wait()
    {
        await using var harness = new ClientHarness(s => s["InventoryApi:MaxRetryAfter"] = "00:00:15");
        harness.Http.Always(HttpStatusCode.TooManyRequests, configure: r => r.Headers.RetryAfter = new(TimeSpan.FromMinutes(10)));

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.False(result.IsSuccess);
        Assert.Equal(InventoryFailureKind.Unavailable, result.Failure.Kind);
        Assert.Equal(TimeSpan.FromMinutes(10), result.Failure.RetryAfter);
        Assert.Single(harness.Http.Requests);
    }

    [Fact]
    public async Task Stops_retrying_when_the_overall_time_budget_for_the_request_runs_out()
    {
        await using var harness = new ClientHarness(s =>
        {
            s["InventoryApi:RequestTimeout"] = "00:00:10";
            s["InventoryApi:MaxRetryAttempts"] = "10";
        });
        harness.Http.Always(HttpStatusCode.TooManyRequests, configure: r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(4)));

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.False(result.IsSuccess);
        Assert.Equal(InventoryFailureKind.Timeout, result.Failure.Kind);

        // Attempts at 0s, 4s and 8s fit in a 10s budget; the ten configured retries do not.
        Assert.InRange(harness.Http.Requests.Count, 2, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("""{"records":[{"id":1,"name":"Truncated""")]
    [InlineData("""{"records":[{"id":"not-a-number"}],"page":1,"pageSize":25,"totalPages":1,"totalRecords":1}""")]
    [InlineData("""{"records":"not-an-array","page":1,"pageSize":25,"totalPages":1,"totalRecords":1}""")]
    public async Task Reports_invalid_data_without_throwing_when_a_200_body_is_not_the_documented_json(string body)
    {
        await using var harness = new ClientHarness();
        harness.Http.Respond(HttpStatusCode.OK, body);

        var result = await harness.RunAsync(harness.Client.GetPageAsync(1, 25, CancellationToken.None));

        Assert.False(result.IsSuccess);
        Assert.Equal(InventoryFailureKind.InvalidData, result.Failure.Kind);
    }

    [Fact]
    public async Task Lets_cancellation_by_the_caller_propagate_instead_of_reporting_it_as_an_upstream_failure()
    {
        await using var harness = new ClientHarness();
        harness.Http.AlwaysHang();
        using var caller = new CancellationTokenSource();

        var pending = harness.Client.GetPageAsync(1, 25, caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(harness.Http.Requests);
    }
}
