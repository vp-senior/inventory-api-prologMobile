using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inventory.Api.Tests;

/// <summary>
/// The whole application (endpoint, aggregation, paging, verification, resilience, HTTP client)
/// running against a simulated upstream. Only the network is replaced.
/// </summary>
public class EndToEndTests(InventoryApiFactory factory) : IClassFixture<InventoryApiFactory>
{
    private const string Endpoint = "/api/inventory/categories";

    /// <summary>
    /// 60 records over three pages of 25, with the kinds of mess the real data has: categories in
    /// mixed case and padding, out-of-stock records, and a category that is entirely out of stock.
    /// </summary>
    private static readonly IReadOnlyList<UpstreamRecord> Dataset = Enumerable.Range(1, 60)
        .Select(i => FakeUpstreamApi.Record(
            id: 1000 + i,
            category: (i % 6) switch
            {
                0 => "Discontinued",
                1 => "Toys",
                2 => "  toys ",
                3 => "TOYS",
                4 => "Outdoor ",
                _ => " outdoor",
            },
            price: 1.25m * i,
            quantity: i % 6 == 0 || i % 10 == 0 ? 0 : i % 7,
            lastUpdated: $"2026-09-{1 + (i * 7 % 28):00}T{i % 24:00}:00:00Z"))
        .ToList();

    [Fact]
    public async Task Aggregates_the_complete_dataset_across_pages()
    {
        var upstream = new FakeUpstreamApi(Dataset);
        var client = CreateClient(upstream);

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Expected figures, computed here independently of the application code.
        var available = Dataset.Where(r => r.Quantity > 0).ToList();
        var toys = available.Where(r => r.Category.Trim().Equals("toys", StringComparison.OrdinalIgnoreCase)).ToList();
        var outdoor = available.Where(r => r.Category.Trim().Equals("outdoor", StringComparison.OrdinalIgnoreCase)).ToList();

        var summary = body.RootElement.GetProperty("summary");
        Assert.Equal(60, summary.GetProperty("totalRecords").GetInt32());
        Assert.Equal(available.Count, summary.GetProperty("availableRecords").GetInt32());
        Assert.Equal(60 - available.Count, summary.GetProperty("excludedRecords").GetInt32());

        var categories = body.RootElement.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(["Outdoor", "Toys"], categories.Select(c => c.GetProperty("category").GetString()));
        AssertCategory(categories[0], outdoor);
        AssertCategory(categories[1], toys);
    }

    [Fact]
    public async Task Serves_repeat_calls_from_the_verified_dataset_without_calling_upstream_again()
    {
        var upstream = new FakeUpstreamApi(Dataset);
        var client = CreateClient(upstream);

        var first = await client.GetStringAsync(Endpoint);
        var requestsAfterFirstCall = upstream.RequestCount;
        var second = await client.GetStringAsync(Endpoint);

        Assert.Equal(4, requestsAfterFirstCall); // three pages, plus one past the end
        Assert.Equal(requestsAfterFirstCall, upstream.RequestCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Rides_out_a_brief_upstream_failure()
    {
        var upstream = new FakeUpstreamApi(Dataset)
        {
            Fault = requestNumber => requestNumber is 2 or 3 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null,
        };

        var response = await CreateClient(upstream).GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(6, upstream.RequestCount);
    }

    [Fact]
    public async Task Answers_503_when_the_upstream_keeps_failing()
    {
        var upstream = new FakeUpstreamApi(Dataset) { Fault = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };

        var response = await CreateClient(upstream).GetAsync(Endpoint);

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Answers_503_when_the_upstream_cannot_be_reached()
    {
        var upstream = new FakeUpstreamApi(Dataset) { Fault = _ => throw new HttpRequestException("Connection refused") };

        var response = await CreateClient(upstream).GetAsync(Endpoint);

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Answers_503_with_retry_after_when_the_upstream_rate_limit_will_not_clear_soon()
    {
        var upstream = new FakeUpstreamApi(Dataset)
        {
            Fault = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new(TimeSpan.FromMinutes(5));
                return response;
            },
        };

        var response = await CreateClient(upstream).GetAsync(Endpoint);

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(TimeSpan.FromMinutes(5), response.Headers.RetryAfter?.Delta);
        Assert.Equal(1, upstream.RequestCount);
    }

    [Fact]
    public async Task Answers_502_when_the_upstream_rejects_the_api_key()
    {
        var upstream = new FakeUpstreamApi(Dataset);
        var client = CreateClient(upstream, builder => builder.UseSetting("InventoryApi:ApiKey", "wrong-key"));

        var response = await client.GetAsync(Endpoint);

        var body = await AssertProblemAsync(response, HttpStatusCode.BadGateway);
        Assert.DoesNotContain("wrong-key", body);
        Assert.Equal(1, upstream.RequestCount); // not retried: it would only fail again
    }

    [Fact]
    public async Task Answers_502_and_says_why_when_the_upstream_data_contradicts_itself()
    {
        // The record repeated at the top of page 2 now differs from its first appearance on page 1.
        var upstream = new FakeUpstreamApi(Dataset)
        {
            Tamper = (page, records) => page == 2 ? [records[0] with { Quantity = records[0].Quantity + 1 }, .. records.Skip(1)] : records,
        };

        var response = await CreateClient(upstream).GetAsync(Endpoint);

        var body = await AssertProblemAsync(response, HttpStatusCode.BadGateway);
        Assert.Contains("Record 1025 was returned with different contents on page 1 and page 2", body);
    }

    [Fact]
    public async Task Answers_502_when_the_upstream_silently_drops_a_record()
    {
        var upstream = new FakeUpstreamApi(Dataset)
        {
            Tamper = (page, records) => page == 3 ? records.Where(r => r.Id != 1057).ToList() : records,
        };

        var response = await CreateClient(upstream).GetAsync(Endpoint);

        await AssertProblemAsync(response, HttpStatusCode.BadGateway);
    }

    [Fact]
    public void Refuses_to_start_without_an_api_key_and_says_how_to_fix_it()
    {
        var misconfigured = factory.WithWebHostBuilder(builder => builder.UseSetting("InventoryApi:ApiKey", ""));

        var exception = Assert.Throws<OptionsValidationException>(() => misconfigured.CreateClient());

        Assert.Contains("InventoryApi:ApiKey is not configured", exception.Message);
        Assert.Contains("user-secrets", exception.Message);
    }

    private HttpClient CreateClient(FakeUpstreamApi upstream, Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            // Keep retries quick, and take pacing out of the picture (it has its own test).
            builder.UseSetting("InventoryApi:RetryBaseDelay", "00:00:00.001");
            builder.UseSetting("InventoryApi:RateLimitPermits", "1000");
            configure?.Invoke(builder);

            builder.ConfigureTestServices(services =>
                services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => upstream)));
        }).CreateClient();

    private static void AssertCategory(JsonElement category, List<UpstreamRecord> expectedRecords)
    {
        Assert.Equal(expectedRecords.Count, category.GetProperty("availableRecordCount").GetInt32());
        Assert.Equal(expectedRecords.Sum(r => r.Price * r.Quantity), category.GetProperty("totalInventoryValue").GetDecimal());
        Assert.Equal(expectedRecords.Select(r => r.Id).Order(), category.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));

        var latest = expectedRecords.OrderByDescending(r => DateTimeOffset.Parse(r.LastUpdated, System.Globalization.CultureInfo.InvariantCulture)).ThenBy(r => r.Id).First();
        Assert.Equal(latest.Id, category.GetProperty("mostRecentlyUpdated").GetProperty("id").GetInt32());
        Assert.Equal(latest.LastUpdated, category.GetProperty("mostRecentlyUpdated").GetProperty("lastUpdated").GetString());
    }

    private static async Task<string> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Exception", body);
        return body;
    }
}
