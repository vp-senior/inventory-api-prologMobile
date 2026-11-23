# What the Inventory API actually does

The assignment says the API is "not perfectly well behaved" and that a 200 is not a guarantee.
Before writing the client, the API was called directly (read-only, roughly a hundred requests on
2026-11-23) to find out how. Every defensive check in the code traces back to a row below, or to
a failure the assignment says must be assumed possible even though it was not seen.

## Observed

| # | Behaviour | Evidence | How the service handles it |
|---|-----------|----------|----------------------------|
| 1 | **Rate limit: 5 requests per 10 seconds**, fixed window. The 6th request gets `429` with a problem body ("Rate limit of 5 requests per 10 seconds exceeded.") and `Retry-After` in whole seconds, rounded up, counting down to the window reset. | A burst of 24 requests: 5 succeeded, 19 were rejected with `Retry-After` 9, 8, 6, 4, 3. Retrying exactly when the header said worked every time. | The client paces itself to 5 per 10 s so it normally never sees a 429. If one arrives anyway it waits for `Retry-After` and retries. |
| 2 | **Every even page repeats the last record of the page before it.** With `pageSize=25`, pages 2, 4 and 6 hold 26 records: ids 1025, 1075 and 1125 each arrive twice. With `pageSize=20` it is 1020, 1060, 1100 and 1140. The copies are identical. | Four full passes at 25 per page, one at 20. | Records are keyed by id. An identical repeat is dropped (and logged). A repeat with different contents rejects the dataset. Summing the pages naively would count three records twice. |
| 3 | `totalRecords` is 157 and `totalPages` is `ceil(157 / pageSize)` (7 at 25, 8 at 20), the same on every page. The sum of the page lengths is 160 at 25 per page, so **page lengths cannot be used to check completeness**. | Same passes. | Completeness is checked on distinct ids: exactly `totalRecords` of them. |
| 4 | **A page past the end returns `200` with an empty `records` array**, not an error. | Pages 8, 9 and 100 at 25 per page. | Used as a check: the service reads page `totalPages + 1` and requires it to be empty. |
| 5 | **Category spelling varies**: 36 raw spellings of 6 categories, differing in case and in leading/trailing spaces (`"Electronics"`, `" Electronics"`, `"  electronics "`, `"ELECTRONICS"`, ...). | Full dataset. | Categories are trimmed, single-spaced and compared without regard to case before grouping. |
| 6 | **21 of the 157 records have `quantity` 0.** One category, Discontinued, has 7 records and all of them are 0. | Full dataset. | Excluded as the assignment requires. Discontinued therefore does not appear in the result at all. |
| 7 | Invalid parameters give `400` with a problem body: `page` below 1 or not a number, `pageSize` outside 1 to 25. | `page=0`, `page=-1`, `page=abc`, `pageSize=0`, `pageSize=26`. | Never sent (page size is validated at startup). A 400 is not retried. |
| 8 | No API key gives `401`. | One request without the header. | Not retried. Reported to the caller as 502, and logged as a configuration problem. |
| 9 | The dataset is static, as documented: four passes returned byte-identical records. | Same passes. | A verified dataset is cached for five minutes. |

## Looked for and not seen

- No `5xx` response and no transport error in any request.
- No slow response: all answered in under 1.2 seconds.
- No missing, null or badly typed fields; no negative price or quantity; every `lastUpdated` has the form `2026-09-23T17:05:00Z`; ids are unique apart from the page-boundary repeats.
- No ties for "most recently updated" within a category.

These are still handled, because the assignment says not to assume that what has not been seen
cannot happen, and because the follow-up interview changes the API's behaviour on purpose.

## Reference figures

Computed independently of the service (a short Python script over the raw pages, deduplicated by
id) and then compared with the service's output against the live API. They match exactly.

| Category | Available records | Total inventory value | Most recently updated |
|----------|------------------:|----------------------:|-----------------------|
| Electronics | 25 | 18253.18 | 1037 at 2026-09-23T17:05:00Z |
| Home & Kitchen | 27 | 17151.51 | 1019 at 2026-09-23T20:36:00Z |
| Office Supplies | 30 | 15996.70 | 1091 at 2026-09-23T06:32:00Z |
| Outdoor | 27 | 17822.93 | 1073 at 2026-09-23T10:03:00Z |
| Toys | 27 | 15879.52 | 1055 at 2026-09-23T13:34:00Z |

157 records in total: 136 available, 21 excluded.
