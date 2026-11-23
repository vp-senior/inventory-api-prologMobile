using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Tests;

public class InventoryApiOptionsValidatorTests
{
    private readonly InventoryApiOptionsValidator _validator = new();

    [Fact]
    public void Defaults_plus_a_base_url_and_key_are_valid()
    {
        Assert.True(_validator.Validate(null, Valid()).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_api_key_is_rejected_with_instructions(string apiKey)
    {
        var options = Valid();
        options.ApiKey = apiKey;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("InventoryApi:ApiKey is not configured", result.FailureMessage);
        Assert.Contains("user-secrets", result.FailureMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("stg.example.com")]
    [InlineData("/api/records")]
    [InlineData("ftp://example.com/")]
    public void A_base_url_that_is_not_absolute_http_is_rejected(string baseUrl)
    {
        var options = Valid();
        options.BaseUrl = baseUrl;

        Assert.Contains("InventoryApi:BaseUrl", _validator.Validate(null, options).FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    public void A_page_size_the_upstream_would_refuse_is_rejected(int pageSize)
    {
        var options = Valid();
        options.PageSize = pageSize;

        Assert.Contains("InventoryApi:PageSize", _validator.Validate(null, options).FailureMessage);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var options = new InventoryApiOptions { PageSize = 0, AttemptTimeout = TimeSpan.Zero, MaxRetryAttempts = 0 };

        var result = _validator.Validate(null, options);

        Assert.Equal(5, result.Failures!.Count()); // base URL, key, page size, retry attempts, attempt timeout
    }

    [Fact]
    public void The_registration_binds_the_InventoryApi_section_and_validates_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InventoryApi:BaseUrl"] = "https://inventory.test/",
                ["InventoryApi:PageSize"] = "20",
                ["InventoryApi:CacheDuration"] = "00:00:30",
                // No ApiKey.
            })
            .Build();
        using var provider = new ServiceCollection().AddInventoryInfrastructure(configuration).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<InventoryApiOptions>>().Value);

        Assert.Contains("InventoryApi:ApiKey is not configured", exception.Message);
    }

    private static InventoryApiOptions Valid() => new() { BaseUrl = "https://inventory.test/", ApiKey = "key" };
}
