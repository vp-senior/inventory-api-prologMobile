using System.Net;
using System.Text;

namespace Inventory.Infrastructure.Tests.Support;

/// <summary>
/// Stands in for the network. Each queued step answers one request, in order; when the queue is
/// empty the fallback set with <see cref="Always"/> answers, and without one the test fails loudly.
/// </summary>
internal sealed class ScriptedHttpHandler(TimeProvider timeProvider) : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly Queue<Step> _steps = new();
    private readonly List<RecordedRequest> _requests = [];
    private Step? _fallback;

    private delegate Task<HttpResponseMessage> Step(HttpRequestMessage request, CancellationToken cancellationToken);

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToList();
            }
        }
    }

    public ScriptedHttpHandler Respond(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? configure = null) =>
        Enqueue(ResponseStep(status, body, configure));

    public ScriptedHttpHandler RespondJson(string body) => Respond(HttpStatusCode.OK, body);

    public ScriptedHttpHandler Throw(Exception exception) => Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Never answers; the request only ends when it is cancelled (by a timeout or the caller).</summary>
    public ScriptedHttpHandler Hang() => Enqueue(HangStep);

    public ScriptedHttpHandler Always(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? configure = null)
    {
        _fallback = ResponseStep(status, body, configure);
        return this;
    }

    public ScriptedHttpHandler AlwaysThrow(Exception exception)
    {
        _fallback = (_, _) => Task.FromException<HttpResponseMessage>(exception);
        return this;
    }

    public ScriptedHttpHandler AlwaysHang()
    {
        _fallback = HangStep;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Step step;
        lock (_lock)
        {
            _requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                timeProvider.GetUtcNow()));

            step = _steps.Count > 0
                ? _steps.Dequeue()
                : _fallback ?? throw new InvalidOperationException($"Unexpected request #{_requests.Count}: {request.RequestUri}");
        }

        return step(request, cancellationToken);
    }

    private ScriptedHttpHandler Enqueue(Step step)
    {
        lock (_lock)
        {
            _steps.Enqueue(step);
        }

        return this;
    }

    private static Step ResponseStep(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure) => (_, _) =>
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        configure?.Invoke(response);
        return Task.FromResult(response);
    };

    private static async Task<HttpResponseMessage> HangStep(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable: an infinite delay only ends by cancellation.");
    }
}

internal sealed record RecordedRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers, DateTimeOffset SentAt);
