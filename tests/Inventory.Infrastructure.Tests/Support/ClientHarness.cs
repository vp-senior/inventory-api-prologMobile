using System.Diagnostics;
using Inventory.Infrastructure.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Inventory.Infrastructure.Tests.Support;

/// <summary>
/// Builds the client through the production registration (<c>AddInventoryInfrastructure</c>), so the
/// real IHttpClientFactory wiring and the real resilience pipeline are under test. Only two things
/// are swapped: the network (a <see cref="ScriptedHttpHandler"/>) and the clock (fake, so backoff
/// and timeouts take no real time).
/// </summary>
internal sealed class ClientHarness : IAsyncDisposable
{
    public const string ApiKey = "test-api-key";

    private readonly ServiceProvider _provider;

    public ClientHarness(Action<Dictionary<string, string?>>? configure = null, bool useRealClock = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["InventoryApi:BaseUrl"] = "https://inventory.test/",
            ["InventoryApi:ApiKey"] = ApiKey,
            ["InventoryApi:MaxRetryAttempts"] = "3",
            ["InventoryApi:RetryBaseDelay"] = "00:00:00.200",
            ["InventoryApi:AttemptTimeout"] = "00:00:02",
            ["InventoryApi:RequestTimeout"] = "00:01:00",
            ["InventoryApi:MaxRetryAfter"] = "00:00:15",
            // Pacing is switched off here and exercised on its own in RateLimitingTests.
            ["InventoryApi:RateLimitPermits"] = "100000",
        };
        configure?.Invoke(settings);

        TimeProvider clock = useRealClock ? TimeProvider.System : Time;
        Http = new ScriptedHttpHandler(clock);

        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddInventoryInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddHttpClient(InventoryApiClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Http);

        _provider = services.BuildServiceProvider();
        Client = _provider.GetRequiredService<IInventoryApiClient>();
    }

    public FakeTimeProvider Time { get; } = new();

    public ScriptedHttpHandler Http { get; }

    public IInventoryApiClient Client { get; }

    /// <summary>
    /// Awaits <paramref name="work"/> while moving the fake clock forward in small steps, so that
    /// backoff delays and timeouts inside the pipeline elapse without real waiting.
    /// </summary>
    public async Task<T> RunAsync<T>(Task<T> work)
    {
        var guard = Stopwatch.StartNew();
        while (!work.IsCompleted)
        {
            if (guard.Elapsed > TimeSpan.FromSeconds(20))
            {
                throw new TimeoutException("The operation did not complete; the pipeline is probably waiting on something the fake clock does not drive.");
            }

            Time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }

        return await work;
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
