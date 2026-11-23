# Inventory Aggregation Service

A .NET 10 service that reads every record from a paginated upstream Inventory API, verifies the
dataset is complete, aggregates it by category and serves the result at
`GET /api/inventory/categories`. `README.md` has the design decisions;
`docs/upstream-observations.md` records how the upstream really behaves.

## Commands

```bash
dotnet build                                   # warnings are errors
dotnet test                                    # all tests; none of them use the network
dotnet test tests/Inventory.Core.Tests         # one project
dotnet test --filter "FullyQualifiedName~PagedInventoryRecordSourceTests"
dotnet run --project src/Inventory.Api         # needs InventoryApi:BaseUrl and InventoryApi:ApiKey
```

## Layout

- `src/Inventory.Core`: business rules. Records, `InventoryAggregator`, `CategoryName`, the
  `IInventoryRecordSource` port, `Result<T>` / `InventoryFailure`.
- `src/Inventory.Infrastructure`: everything about the upstream API. `Http/` (client, resilience
  pipeline), `Paging/` (page validation, record mapping, the paged source), `Caching/`.
- `src/Inventory.Api`: the host. Controller, response contracts, failure-to-status mapping.
- `tests/`: one test project per source project.

## Rules

- `Inventory.Core` has no package or project references. Do not add any. Nothing in it may
  mention HTTP, JSON or configuration.
- Expected failures (upstream down, bad data, timeouts) are returned as `Result<T>` with an
  `InventoryFailure`. Do not throw for them. Exceptions are for bugs.
- Never return a partial dataset as a success. If completeness cannot be shown, return
  `InventoryFailureKind.InvalidData` and say exactly which check failed.
- `HttpClient` comes from `IHttpClientFactory.CreateClient(...)` at the point of use. Do not
  store an `HttpClient` in a field of a singleton.
- No blocking on async code (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`).
- Secrets and the upstream base URL never go in source control, in tests, or in documentation.
  Tests use `https://inventory.test/` and a fake key.
- Time comes from the injected `TimeProvider`, so tests can control it. Do not call
  `DateTime.UtcNow` or `Task.Delay` without it.

## Tests

- Every bug fix and every defensive check needs a test that fails without it. Check that it
  does: remove the fix, watch the test fail, put the fix back.
- Test names are sentences describing behaviour, with underscores:
  `Rejects_the_dataset_when_a_repeated_record_differs_between_pages`.
- Client tests go through the real DI registration (`ClientHarness`) with a scripted handler and
  a fake clock. Do not assert an upper bound on elapsed time, and do not assert a lower bound on a
  jittered backoff delay.
- Plain xUnit assertions; no assertion or mocking libraries.
