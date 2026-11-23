using Inventory.Infrastructure.Tests.Support;

namespace Inventory.Infrastructure.Tests.Http;

public class RateLimitingTests
{
    /// <summary>
    /// The limiter runs on real timers, so this test uses the real clock with a short window.
    /// Without the limiter the third request would follow within a millisecond or two, so a
    /// generous tolerance still separates "paced" from "not paced" without being timing-sensitive.
    /// </summary>
    [Fact]
    public async Task Holds_requests_back_once_the_upstream_limit_for_the_window_is_used_up()
    {
        var window = TimeSpan.FromMilliseconds(400);
        await using var harness = new ClientHarness(
            s =>
            {
                s["InventoryApi:RateLimitPermits"] = "2";
                s["InventoryApi:RateLimitWindow"] = window.ToString();
            },
            useRealClock: true);
        harness.Http.Always(System.Net.HttpStatusCode.OK, Wire.Json(Wire.Page(Wire.Dataset(1), 1, 25)));

        for (var i = 0; i < 3; i++)
        {
            var result = await harness.Client.GetPageAsync(1, 25, CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        var sentAt = harness.Http.Requests.Select(r => r.SentAt).ToList();
        var thirdAfterFirst = sentAt[2] - sentAt[0];
        Assert.True(
            thirdAfterFirst >= window * 0.75,
            $"The third request went out {thirdAfterFirst.TotalMilliseconds:F0} ms after the first; it should have been held for about one {window.TotalMilliseconds:F0} ms window.");
    }
}
