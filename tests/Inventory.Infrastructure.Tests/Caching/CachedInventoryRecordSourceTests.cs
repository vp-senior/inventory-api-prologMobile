using Inventory.Core;
using Inventory.Core.Results;
using Inventory.Infrastructure.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Inventory.Infrastructure.Tests.Caching;

public class CachedInventoryRecordSourceTests
{
    private static readonly InventoryFailure Down = new(InventoryFailureKind.Unavailable, "down");

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Reuses_a_verified_dataset_until_it_expires()
    {
        var inner = new StubSource(_ => Task.FromResult(Snapshot()));
        var source = CreateSource(inner, cacheDuration: TimeSpan.FromMinutes(5));

        var first = await source.GetSnapshotAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(4));
        var second = await source.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(1, inner.Calls);
        Assert.Same(first.Value, second.Value);
    }

    [Fact]
    public async Task Collects_again_once_the_cached_dataset_has_expired()
    {
        var inner = new StubSource(_ => Task.FromResult(Snapshot()));
        var source = CreateSource(inner, cacheDuration: TimeSpan.FromMinutes(5));

        await source.GetSnapshotAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        await source.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Never_caches_a_failure()
    {
        var answers = new Queue<Result<InventorySnapshot>>([Down, Snapshot()]);
        var inner = new StubSource(_ => Task.FromResult(answers.Dequeue()));
        var source = CreateSource(inner);

        var first = await source.GetSnapshotAsync(CancellationToken.None);
        var second = await source.GetSnapshotAsync(CancellationToken.None);

        Assert.False(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Does_not_cache_at_all_when_the_cache_duration_is_zero()
    {
        var inner = new StubSource(_ => Task.FromResult(Snapshot()));
        var source = CreateSource(inner, cacheDuration: TimeSpan.Zero);

        await source.GetSnapshotAsync(CancellationToken.None);
        await source.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Callers_arriving_during_a_collection_share_it_instead_of_starting_their_own()
    {
        var release = new TaskCompletionSource<Result<InventorySnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new StubSource(_ => release.Task);
        var source = CreateSource(inner);

        var callers = Enumerable.Range(0, 5).Select(_ => source.GetSnapshotAsync(CancellationToken.None)).ToArray();
        await inner.Started;
        release.SetResult(Snapshot());
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, inner.Calls);
        Assert.All(results, result => Assert.Same(results[0].Value, result.Value));
    }

    [Fact]
    public async Task Callers_sharing_a_collection_all_receive_its_failure()
    {
        var release = new TaskCompletionSource<Result<InventorySnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new StubSource(_ => release.Task);
        var source = CreateSource(inner);

        var callers = Enumerable.Range(0, 3).Select(_ => source.GetSnapshotAsync(CancellationToken.None)).ToArray();
        await inner.Started;
        release.SetResult(Down);
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, inner.Calls);
        Assert.All(results, result => Assert.Same(Down, result.Failure));
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_collection_for_everyone_else()
    {
        var release = new TaskCompletionSource<Result<InventorySnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new StubSource(_ => release.Task);
        var source = CreateSource(inner);
        using var impatient = new CancellationTokenSource();

        var impatientCaller = source.GetSnapshotAsync(impatient.Token);
        var patientCaller = source.GetSnapshotAsync(CancellationToken.None);
        await inner.Started;

        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatientCaller);
        Assert.False(inner.LastToken.IsCancellationRequested);

        release.SetResult(Snapshot());
        Assert.True((await patientCaller).IsSuccess);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Reports_a_timeout_and_cancels_the_collection_when_it_overruns_its_budget()
    {
        var inner = new StubSource(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Snapshot();
        });
        var source = CreateSource(inner, snapshotTimeout: TimeSpan.FromSeconds(90));

        var pending = source.GetSnapshotAsync(CancellationToken.None);
        await inner.Started;
        _time.Advance(TimeSpan.FromSeconds(90));
        var result = await pending;

        Assert.False(result.IsSuccess);
        Assert.Equal(InventoryFailureKind.Timeout, result.Failure.Kind);
        Assert.True(inner.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Recovers_after_an_unexpected_exception_instead_of_staying_stuck_on_it()
    {
        var attempts = 0;
        var inner = new StubSource(_ => ++attempts == 1
            ? Task.FromException<Result<InventorySnapshot>>(new InvalidOperationException("bug"))
            : Task.FromResult(Snapshot()));
        var source = CreateSource(inner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetSnapshotAsync(CancellationToken.None));
        var second = await source.GetSnapshotAsync(CancellationToken.None);

        Assert.True(second.IsSuccess);
    }

    private CachedInventoryRecordSource CreateSource(
        IInventoryRecordSource inner, TimeSpan? cacheDuration = null, TimeSpan? snapshotTimeout = null) =>
        new(
            inner,
            Options.Create(new InventoryApiOptions
            {
                CacheDuration = cacheDuration ?? TimeSpan.FromMinutes(5),
                SnapshotTimeout = snapshotTimeout ?? TimeSpan.FromSeconds(90),
            }),
            _time,
            NullLogger<CachedInventoryRecordSource>.Instance);

    private Result<InventorySnapshot> Snapshot() =>
        new InventorySnapshot([new InventoryRecord(1, "Item 1", "Toys", 1m, 1, _time.GetUtcNow())], _time.GetUtcNow());

    private sealed class StubSource(Func<CancellationToken, Task<Result<InventorySnapshot>>> answer) : IInventoryRecordSource
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Completes when the wrapped source has been called at least once.</summary>
        public Task Started => _started.Task;

        public CancellationToken LastToken { get; private set; }

        public Task<Result<InventorySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastToken = cancellationToken;
            _started.TrySetResult();
            return answer(cancellationToken);
        }
    }
}
