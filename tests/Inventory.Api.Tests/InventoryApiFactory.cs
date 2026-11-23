using Inventory.Core;
using Inventory.Core.Results;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inventory.Api.Tests;

/// <summary>
/// Hosts the real application in memory. Configuration that would normally come from user secrets
/// is supplied here; nothing in these tests talks to the network.
/// </summary>
public class InventoryApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("InventoryApi:BaseUrl", "https://inventory.test/");
        builder.UseSetting("InventoryApi:ApiKey", "test-api-key");
    }
}

internal static class InventoryApiFactoryExtensions
{
    /// <summary>The application with its inventory source replaced by one that returns <paramref name="answer"/>.</summary>
    public static HttpClient CreateClientWithSource(
        this WebApplicationFactory<Program> factory, Func<Task<Result<InventorySnapshot>>> answer) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IInventoryRecordSource>();
            services.AddSingleton<IInventoryRecordSource>(new StubSource(answer));
        })).CreateClient();

    private sealed class StubSource(Func<Task<Result<InventorySnapshot>>> answer) : IInventoryRecordSource
    {
        public Task<Result<InventorySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken) => answer();
    }
}
