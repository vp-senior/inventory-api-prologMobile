using System.Net.Http.Headers;
using Inventory.Core;
using Inventory.Infrastructure.Caching;
using Inventory.Infrastructure.Http;
using Inventory.Infrastructure.Paging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything needed to read inventory from the upstream API, exposed to the rest of
    /// the application only as <see cref="IInventoryRecordSource"/>.
    /// </summary>
    public static IServiceCollection AddInventoryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InventoryApiOptions>()
            .Bind(configuration.GetSection(InventoryApiOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<InventoryApiOptions>, InventoryApiOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);

        services
            .AddHttpClient(InventoryApiClient.HttpClientName, (serviceProvider, httpClient) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<InventoryApiOptions>>().Value;

                // A trailing slash makes relative request paths resolve under the base path.
                httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                httpClient.DefaultRequestHeaders.Add(options.ApiKeyHeaderName, options.ApiKey);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Timeouts are owned by the resilience pipeline; HttpClient's own 100-second default
                // would otherwise be a second, hidden limit.
                httpClient.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddResilienceHandler(InventoryApiResilience.PipelineName, (pipeline, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<InventoryApiOptions>>().Value;
                var timeProvider = context.ServiceProvider.GetRequiredService<TimeProvider>();

                // The pipeline is built once and shared by every handler the factory creates, so the
                // limiter's state survives handler recycling.
                var rateLimiter = InventoryApiResilience.CreateRateLimiter(options);
                context.OnPipelineDisposed(rateLimiter.Dispose);

                InventoryApiResilience.Configure(pipeline, options, rateLimiter, timeProvider);
            });

        // All singletons: none of them holds an HttpClient (the client asks the factory per call),
        // and the cache must outlive individual requests.
        services.AddSingleton<IInventoryApiClient, InventoryApiClient>();
        services.AddSingleton<PagedInventoryRecordSource>();
        services.AddSingleton<IInventoryRecordSource>(serviceProvider => new CachedInventoryRecordSource(
            inner: serviceProvider.GetRequiredService<PagedInventoryRecordSource>(),
            serviceProvider.GetRequiredService<IOptions<InventoryApiOptions>>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ILogger<CachedInventoryRecordSource>>()));

        return services;
    }
}
