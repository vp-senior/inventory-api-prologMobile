# Inventory Aggregation Service

A small .NET 10 service that reads every record from the paginated Inventory API, checks that
what it collected is complete and consistent, aggregates the records by category, and serves the
result from one endpoint:

```
GET /api/inventory/categories
```

Contents: [Setup and run](#setup-and-run) · [The endpoint](#the-endpoint) ·
[Architecture](#architecture) · [Getting a complete and correct dataset](#getting-a-complete-and-correct-dataset) ·
[Decisions](#decisions) · [Assumptions and known limitations](#assumptions-and-known-limitations)

## Setup and run

**Prerequisite:** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Nothing else.

### 1. Configure the upstream API

The base URL and API key from the assignment email are not in this repository. Supply them in
one of two ways.

**User secrets** (stored in your user profile, outside the repository):

```bash
dotnet user-secrets set "InventoryApi:BaseUrl" "<base URL>" --project src/Inventory.Api
dotnet user-secrets set "InventoryApi:ApiKey"  "<API key>"  --project src/Inventory.Api
```

**Or environment variables** (note the double underscore):

```bash
# bash / zsh
export InventoryApi__BaseUrl="<base URL>"
export InventoryApi__ApiKey="<API key>"
```

```powershell
# PowerShell
$env:InventoryApi__BaseUrl = "<base URL>"
$env:InventoryApi__ApiKey  = "<API key>"
```

If either is missing the application refuses to start and says which one.

### 2. Run

```bash
dotnet run --project src/Inventory.Api
```

It listens on `http://localhost:5242`.

### 3. Call the endpoint

```bash
curl http://localhost:5242/api/inventory/categories
```

**The first call takes about 11 seconds.** The upstream API allows 5 requests per 10 seconds and
a complete read needs 8, so the service has to wait for the second window. The verified dataset
is then cached for 5 minutes and later calls answer in milliseconds.

### 4. Run the tests

```bash
dotnet test
```

145 tests, about ten seconds. None of them use the network or need the API key.

## The endpoint

`GET /api/inventory/categories` returns `200`:

```json
{
  "summary": {
    "retrievedAt": "2026-11-23T19:24:00.8509474Z",
    "totalRecords": 157,
    "availableRecords": 136,
    "excludedRecords": 21,
    "categoryCount": 5
  },
  "categories": [
    {
      "category": "Electronics",
      "totalInventoryValue": 18253.18,
      "availableRecordCount": 25,
      "mostRecentlyUpdated": {
        "id": 1037,
        "name": "Barcode Scanner MK-1037",
        "lastUpdated": "2026-09-23T17:05:00Z"
      },
      "items": [
        {
          "id": 1016,
          "name": "Label Printer MK-1016",
          "price": 53.74,
          "quantity": 14,
          "lastUpdated": "2026-09-20T01:10:00Z"
        }
      ]
    }
  ]
}
```

(`items` is shortened here; the real response lists all 25.)

**Differences from the suggested shape, all deliberate:**

- A `summary` object is added. It lets a caller reconcile the result against the source
  (`availableRecords + excludedRecords = totalRecords`, and `availableRecords` is the sum of the
  categories' counts) and shows how old the data is, since results are cached.
- `categories` are ordered by name and `items` by id, so the same data always produces the same
  response.

**When something goes wrong** the response is an RFC 9457 `application/problem+json` body. This
service sits in front of another API, so it uses the gateway status codes; none of these failures
is the caller's fault, so none is a 4xx.

| Situation | Status | Notes |
|-----------|--------|-------|
| Upstream unreachable, or still failing (5xx, 429) after retries | `503` | `Retry-After` is set when the upstream named a wait |
| Upstream did not answer within the time budget | `504` | |
| Upstream refused the request (bad API key, 4xx, unexpected status) | `502` | A configuration problem on this side; never retried |
| Upstream answered, but the data is malformed, incomplete or contradicts itself | `502` | An `errors` array lists each check that failed |
| A bug in this service | `500` | No exception text or stack trace, in any environment |

Example:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.3",
  "title": "The inventory API returned data that failed verification",
  "status": 502,
  "detail": "The inventory API returned a dataset that is incomplete or contradicts itself.",
  "errors": [
    "Record 1025 was returned with different contents on page 1 and page 2."
  ],
  "traceId": "00-5f0c1e…-00"
}
```

## Architecture

Three projects, with dependencies pointing one way: `Api → Infrastructure → Core`.

```
Inventory.Api             Controller, JSON contracts, failure → status code mapping, startup
Inventory.Infrastructure  Everything about the upstream API
  Http/                     InventoryApiClient, the resilience pipeline
  Paging/                   Page validation, record mapping, PagedInventoryRecordSource
  Caching/                  CachedInventoryRecordSource
Inventory.Core            Business rules: InventoryAggregator, CategoryName,
                          IInventoryRecordSource, Result<T>
```

**Core** holds the assignment's rules. `InventoryAggregator` is a pure function from records to
per-category results. Core defines the one thing it needs from the outside world,
`IInventoryRecordSource` ("give me the complete dataset, or tell me why you cannot"), and has no
package or project references at all. The requirement that aggregation must not know HTTP exists
is therefore enforced by the compiler rather than by convention.

**Infrastructure** implements that interface in three layers, each with one job:

1. `InventoryApiClient` is the only class that speaks HTTP. It fetches one page and turns every
   possible outcome (success, error status, transport failure, timeout, unreadable body) into a
   value. Retries, pacing and timeouts live in the `HttpClient` handler pipeline around it.
2. `PagedInventoryRecordSource` walks the pages and decides whether the result can be trusted.
   It knows nothing about status codes.
3. `CachedInventoryRecordSource` wraps it so that collection happens once at a time and the
   result is reused.

The split between 1 and 2 follows the two different ways the upstream can fail: a request can
fail (handled by retrying the request), or it can succeed and still be wrong (handled by
checking the content). Mixing them makes both harder to test; apart, the paging logic is tested
with an in-memory fake and no HTTP, and the HTTP behaviour is tested without any paging.

**Api** is thin: one controller action that calls the service and maps the result to JSON or to
a problem response.

**Failures are values, not exceptions.** An upstream outage is an expected event for this
service, so it travels as `Result<T>` carrying an `InventoryFailure` (kind, message, details,
retry-after) from the client all the way to the controller, where one `switch` maps it to a
status code. Exceptions are left for bugs, and a global handler turns those into a plain 500.

**`HttpClient` usage.** `InventoryApiClient` takes `IHttpClientFactory` and calls
`CreateClient("InventoryApi")` for each request. The services are singletons (the cache has to
be), and a singleton that stored an `HttpClient` would pin one connection handler for the life of
the process; asking the factory each time lets it pool and recycle handlers as designed.

## Getting a complete and correct dataset

Before writing the client I called the API directly to see how it misbehaves. The findings, with
evidence, are in [docs/upstream-observations.md](docs/upstream-observations.md). The ones that
shaped the design:

- **It rate limits at 5 requests per 10 seconds** (`429` with `Retry-After`). A full read needs 8.
- **Every even page repeats the last record of the previous page.** At 25 per page, three records
  arrive twice. Summing the pages would double-count them.
- **Category names are spelled 36 ways for 6 categories** (case and padding).
- **One category, Discontinued, consists only of zero-quantity records.**
- A page past the end returns `200` with no records.

### Request failures

Handled by a resilience pipeline built with `Microsoft.Extensions.Http.Resilience` (Polly). It is
configured strategy by strategy in
[`InventoryApiResilience.cs`](src/Inventory.Infrastructure/Http/InventoryApiResilience.cs) rather
than with the library's preset, so what happens on a failure can be read in one place:

| Order | Strategy | What it does |
|------:|----------|--------------|
| 1 | Request timeout (45 s) | Caps one page including all retries and waits |
| 2 | Retry (3 retries) | Only for failures that may not repeat: 5xx, 408, 429, connection errors, attempt timeouts. Exponential backoff with jitter from 0.5 s. `Retry-After` overrides the backoff when present |
| 3 | Rate limiter | Paces requests to 5 per 10 s so the service queues instead of provoking 429s |
| 4 | Attempt timeout (10 s) | Caps a single attempt; a timed-out attempt is retried |

Not retried: 400, 401, 403, 404 and any other status that would simply repeat. Also, if
`Retry-After` asks for more than 15 seconds the service does not hold the caller that long; it
fails at once with `503` and passes the `Retry-After` on.

### Responses that are 200 but wrong

`PagedInventoryRecordSource` returns a dataset only if all of these hold. Otherwise the endpoint
answers `502` and lists what failed.

1. **Each page answers the question asked**: `page` and `pageSize` echo the request; `totalPages`
   equals `ceil(totalRecords / pageSize)`; the page holds at least the records that belong on it.
2. **Every record meets the documented contract**: id, name and category present; price and
   quantity not negative; `lastUpdated` a valid ISO 8601 date-time.
3. **All pages agree on the totals** reported by page 1.
4. **Repeated records are identical.** Identical copies are dropped and logged. If two copies of
   one id differ, the dataset is rejected.
5. **The number of distinct records equals `totalRecords`.**
6. **The page after the last one is empty**, so `totalPages` is not understating the data.

A page that fails check 1 or 2, or whose body cannot be parsed, is requested again (up to 3
attempts in total). The dataset is static, so a good answer on the second try is as trustworthy
as one on the first. Checks 3 to 6 are about the dataset as a whole and are not retried.

### How this was verified

- Against the live API: the service's output matches figures computed separately with a short
  Python script over the raw pages (table in the observations document).
- Thirty safeguards (each check, retry rule and timeout) were removed one at a time to see
  whether a test fails without it. For 28 one did. The other two removals changed no behaviour,
  because the code is redundant there: the explicit `ThenBy(id)` tie-break (the list is already
  in id order and the sort is stable), and the "only cache successes" condition (a failure has
  no dataset to cache). I kept both because they state the intent.

## Decisions

Places where the assignment left a choice open, and what I chose.

1. **A category with no available stock is omitted.** The rules say to exclude zero-quantity
   records and then group what remains, so Discontinued (7 records, all zero) produces no group.
   It is not listed with a count of 0. The excluded records are still visible in
   `summary.excludedRecords`.

2. **Category names are normalised before grouping**: trimmed, inner whitespace collapsed,
   compared without regard to case, and displayed in title case (`"  home & kitchen "` becomes
   `"Home & Kitchen"`). Without this the result has 36 categories instead of 5. Title case was
   chosen over "most common spelling" because it depends only on the value, not on what else is
   in the dataset. Its weakness: an acronym such as "TV" would be shown as "Tv".

3. **Ties for "most recently updated" go to the lowest id.** The real data has no ties; the rule
   exists so the answer is deterministic if it ever does.

4. **Money uses `decimal` and is rounded only in the response**, to two places, half away from
   zero. Item prices are passed through exactly as received.

5. **Identical duplicates are dropped; conflicting duplicates reject the dataset.** If the same
   id arrives with two different contents there is no sound way to choose, and "last one wins"
   would silently depend on page order.

6. **Fail closed: no partial results.** If completeness cannot be shown, the endpoint returns an
   error rather than figures that might be wrong. For inventory value, a confident wrong number
   seemed worse than an honest failure. The cost is availability: one bad record fails the whole
   response (see limitations).

7. **A negative quantity or price is treated as bad data**, not as "unavailable". The contract
   says quantity is "units on hand; may be 0". A negative value suggests something upstream is
   broken, and I would rather report that than quietly filter it.

8. **Timestamps without an offset are read as UTC**, because the contract says `lastUpdated` is
   UTC. All timestamps are returned in UTC with a `Z` suffix.

9. **Page size 25, pages read one after another.** 25 is the maximum, so it needs the fewest
   rate-limited calls. Parallel requests would gain nothing against a limit of 5 per 10 seconds
   and would make logs harder to follow.

10. **Results are cached for 5 minutes, and concurrent callers share one collection.** The
    dataset is documented as static, so caching is safe; 5 minutes rather than forever in case
    that stops being true. Failures are never cached. Without sharing, ten simultaneous callers
    would start ten collections competing for the same 5 requests per 10 seconds.

11. **No circuit breaker.** The cache and the shared collection already bound the load this
    service can put on the upstream to one collection at a time, which is the main thing a
    breaker would add at this traffic level. I left it out rather than add a strategy I could
    not justify.

12. **Plain xUnit assertions, no mocking library.** The fakes are small hand-written classes
    (`ScriptedHttpHandler`, `FakeInventoryApi`) that read like the scenario they set up.

## Assumptions and known limitations

**Assumptions**

- The rate limit is 5 per 10 seconds as observed. It is configurable
  (`InventoryApi:RateLimitPermits`, `RateLimitWindow`) but not discovered at runtime. If the
  upstream lowers it, the service still works, by way of `429` and `Retry-After`, only slower.
- I do not know whether the limit is per API key or per client address. If the key is shared with
  other consumers, this service will see more 429s than its own pacing predicts.
- `totalPages` is expected to equal `ceil(totalRecords / pageSize)`. An API that reports
  `totalPages: 1` for an empty dataset would be rejected. I could not observe that case.
- Ids fit in a 32-bit integer.

**Limitations I know about**

- **The first call is slow (about 11 s)**, and so is the first call after the cache expires. The
  caller waits for the whole collection. Warming the cache at startup, or serving the previous
  dataset while refreshing in the background, would remove the wait. I stopped short of that.
- **The cache is in memory and per process.** Two instances would each collect separately and
  share one upstream rate limit without knowing about each other. A real deployment would need a
  shared cache (Redis) and probably a shared limiter.
- **When the upstream is down, every call pays for the retries** (a few seconds) before getting
  its 503, because failures are not cached. Callers arriving at the same time share one attempt;
  callers arriving one after another do not.
- **Strictness has a price.** One record with a missing name fails the entire response. That is
  the trade-off in decision 6. If the business would rather have figures with a warning, the
  change is contained: collect problem records in `PageValidator` instead of failing, and add a
  `warnings` list to the response.
- **The client-side limiter is approximate.** It is the .NET sliding-window limiter, which frees
  permits a segment at a time. I padded the window by one segment so it does not release early,
  but timer scheduling can still be a few milliseconds off. A 429 that slips through is handled
  by the retry, so this affects speed, not correctness.
- **Category normalisation only fixes case and spacing.** "Home and Kitchen" and
  "Home & Kitchen" would be two categories.
- **The response is not paged.** All 136 items come back in one body (about 20 KB). Fine for this
  dataset; not for one a thousand times larger.
- **The rate-limiter test uses the real clock** (about half a second), because the .NET limiter
  does not accept a fake one. Everything else runs on a fake clock.
- **No automated test calls the live API.** It needs the secret and is rate limited, so that
  check was done by hand and recorded in the observations document.

**Not built, and would be next for a production service**

- Authentication and rate limiting on this service's own endpoint.
- Health checks, metrics and tracing (OpenTelemetry). Today there are only logs.
- An OpenAPI document.
- HTTPS configuration and a container image.

## AI assistance

Built with Claude Code, as the assignment permits.
