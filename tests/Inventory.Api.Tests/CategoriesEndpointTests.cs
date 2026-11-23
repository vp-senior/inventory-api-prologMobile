using System.Net;
using System.Text.Json;
using Inventory.Core;
using Inventory.Core.Results;
using Microsoft.AspNetCore.Hosting;

namespace Inventory.Api.Tests;

public class CategoriesEndpointTests(InventoryApiFactory factory) : IClassFixture<InventoryApiFactory>
{
    private const string Endpoint = "/api/inventory/categories";

    private static readonly DateTimeOffset RetrievedAt = new(2026, 11, 23, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Returns_the_aggregated_categories_in_the_documented_shape()
    {
        var client = factory.CreateClientWithSource(() => Success(
            new InventoryRecord(1001, "Thermal Receipt Printer", " electronics ", 149.99m, 12, At(15, 12, 30)),
            new InventoryRecord(1002, "Label Printer", "ELECTRONICS", 20.005m, 1, At(14, 8, 0)),
            new InventoryRecord(1003, "Old Stock", "Discontinued", 5.00m, 0, At(16, 0, 0))));

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        // 149.99 x 12 + 20.005 x 1 = 1819.885, presented as 1819.89.
        AssertJsonEqual(
            """
            {
              "summary": {
                "retrievedAt": "2026-11-23T09:30:00Z",
                "totalRecords": 3,
                "availableRecords": 2,
                "excludedRecords": 1,
                "categoryCount": 1
              },
              "categories": [
                {
                  "category": "Electronics",
                  "totalInventoryValue": 1819.89,
                  "availableRecordCount": 2,
                  "mostRecentlyUpdated": { "id": 1001, "name": "Thermal Receipt Printer", "lastUpdated": "2026-10-15T12:30:00Z" },
                  "items": [
                    { "id": 1001, "name": "Thermal Receipt Printer", "price": 149.99, "quantity": 12, "lastUpdated": "2026-10-15T12:30:00Z" },
                    { "id": 1002, "name": "Label Printer", "price": 20.005, "quantity": 1, "lastUpdated": "2026-10-14T08:00:00Z" }
                  ]
                }
              ]
            }
            """,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Returns_an_empty_list_rather_than_an_error_when_nothing_is_in_stock()
    {
        var client = factory.CreateClientWithSource(() => Success());

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("categories").GetArrayLength());
    }

    [Theory]
    [InlineData(InventoryFailureKind.Unavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(InventoryFailureKind.Timeout, HttpStatusCode.GatewayTimeout)]
    [InlineData(InventoryFailureKind.Rejected, HttpStatusCode.BadGateway)]
    [InlineData(InventoryFailureKind.InvalidData, HttpStatusCode.BadGateway)]
    public async Task Answers_an_upstream_failure_with_a_problem_response(InventoryFailureKind kind, HttpStatusCode expectedStatus)
    {
        var client = factory.CreateClientWithSource(() => Failure(new InventoryFailure(kind, "What went wrong, in one sentence.")));

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)expectedStatus, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("What went wrong, in one sentence.", body.RootElement.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Tells_the_caller_when_to_come_back_if_the_upstream_said_so()
    {
        var client = factory.CreateClientWithSource(() => Failure(
            new InventoryFailure(InventoryFailureKind.Unavailable, "Rate limited.") { RetryAfter = TimeSpan.FromSeconds(8.2) }));

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(9), response.Headers.RetryAfter?.Delta); // rounded up: never invite an early retry
    }

    [Fact]
    public async Task Lists_each_failed_check_when_the_upstream_data_could_not_be_verified()
    {
        var client = factory.CreateClientWithSource(() => Failure(
            new InventoryFailure(InventoryFailureKind.InvalidData, "The dataset is incomplete.")
            {
                Details = ["The API reports 157 records, but its 7 pages contained 156 distinct records."],
            }));

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Contains("156 distinct records", error.GetString());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task An_unexpected_exception_becomes_a_500_problem_response_that_reveals_nothing(string environment)
    {
        var client = factory
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment))
            .CreateClientWithSource(() => throw new InvalidOperationException("secret-internal-detail"));

        var response = await client.GetAsync(Endpoint);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("secret-internal-detail", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("   at ", body); // no stack trace
    }

    [Fact]
    public async Task An_unknown_route_gets_a_problem_response_too()
    {
        var response = await factory.CreateClient().GetAsync("/api/inventory/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private static Task<Result<InventorySnapshot>> Success(params InventoryRecord[] records) =>
        Task.FromResult<Result<InventorySnapshot>>(new InventorySnapshot(records, RetrievedAt));

    private static Task<Result<InventorySnapshot>> Failure(InventoryFailure failure) =>
        Task.FromResult<Result<InventorySnapshot>>(failure);

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedJson = JsonDocument.Parse(expected);
        using var actualJson = JsonDocument.Parse(actual);

        Assert.True(
            JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement),
            $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
    }
}
