using System.Globalization;
using Inventory.Core;
using Inventory.Infrastructure.Http;

namespace Inventory.Infrastructure.Paging;

/// <summary>
/// Turns a wire record into a domain record, but only if it satisfies the documented contract.
/// Nothing that fails here ever reaches the aggregation.
/// </summary>
internal static class InventoryRecordMapper
{
    // ISO 8601 date-time with seconds, optional fraction, and "Z" or a numeric offset.
    private const string Iso8601Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK";

    /// <summary>
    /// Returns the mapped record, or null after adding one entry to <paramref name="problems"/>
    /// for each rule the record breaks. <paramref name="position"/> is the record's 1-based place on its page.
    /// </summary>
    public static InventoryRecord? Map(RecordDto? dto, int position, List<string> problems)
    {
        if (dto is null)
        {
            problems.Add($"Record #{position} is null.");
            return null;
        }

        var label = dto.Id is { } id ? $"Record {id}" : $"Record #{position}";
        var problemsBefore = problems.Count;

        if (dto.Id is null)
        {
            problems.Add($"{label} has no id.");
        }

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            problems.Add($"{label} has no name.");
        }

        if (string.IsNullOrWhiteSpace(dto.Category))
        {
            problems.Add($"{label} has no category.");
        }

        if (dto.Price is null or < 0)
        {
            problems.Add($"{label} has an invalid price ({Describe(dto.Price)}); expected a non-negative amount.");
        }

        if (dto.Quantity is null or < 0)
        {
            problems.Add($"{label} has an invalid quantity ({Describe(dto.Quantity)}); expected zero or more.");
        }

        if (!TryParseTimestamp(dto.LastUpdated, out var lastUpdated))
        {
            problems.Add($"{label} has an invalid lastUpdated ({Describe(dto.LastUpdated)}); expected an ISO 8601 date-time.");
        }

        return problems.Count > problemsBefore
            ? null
            : new InventoryRecord(dto.Id!.Value, dto.Name!, dto.Category!, dto.Price!.Value, dto.Quantity!.Value, lastUpdated);
    }

    /// <summary>
    /// The contract says timestamps are UTC, so a value without an offset is read as UTC rather than
    /// as this machine's local time. Everything is normalised to UTC.
    /// </summary>
    internal static bool TryParseTimestamp(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(
            text,
            Iso8601Format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);

    private static string Describe<T>(T? value) =>
        value is null ? "missing" : string.Create(CultureInfo.InvariantCulture, $"'{value}'");
}
