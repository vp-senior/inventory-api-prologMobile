using Inventory.Core;
using Inventory.Core.Aggregation;
using Inventory.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Every error this service returns, expected or not, has the same RFC 9457 problem+json shape.
builder.Services.AddProblemDetails();

// Business logic: knows nothing about HTTP, depends only on IInventoryRecordSource.
builder.Services.AddSingleton<InventoryAggregator>();
builder.Services.AddSingleton<InventoryReportService>();

// Upstream access: HTTP client, resilience, paging, verification and caching behind IInventoryRecordSource.
// Its options are validated at startup, so a missing API key stops the application here.
builder.Services.AddInventoryInfrastructure(builder.Configuration);

var app = builder.Build();

// Last line of defence. Expected failures are returned as values and mapped in the controller;
// this only catches bugs, and answers with a problem response that carries no exception detail.
app.UseExceptionHandler();

// Gives bodiless error responses (such as a 404 for an unknown route) a problem body too.
app.UseStatusCodePages();

app.MapControllers();

app.Run();
