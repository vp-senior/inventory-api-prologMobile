namespace Inventory.Core.Results;

/// <summary>An expected failure, carried as a value rather than thrown as an exception.</summary>
/// <param name="Kind">The category of failure; callers decide how to react based on this.</param>
/// <param name="Message">A one-sentence summary that is safe to show to an API caller.</param>
public sealed record InventoryFailure(InventoryFailureKind Kind, string Message)
{
    /// <summary>The specific problems found, for example each integrity check that failed.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>How long the source asked us to wait before trying again, when it said so.</summary>
    public TimeSpan? RetryAfter { get; init; }
}
