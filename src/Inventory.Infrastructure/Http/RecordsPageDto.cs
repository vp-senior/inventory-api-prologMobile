namespace Inventory.Infrastructure.Http;

/// <summary>
/// One page exactly as the upstream API sends it. Every member is nullable because a 200 response
/// is not proof that the body is well formed; <see cref="Paging.PageValidator"/> decides what is acceptable.
/// </summary>
internal sealed record RecordsPageDto(
    IReadOnlyList<RecordDto?>? Records,
    int? Page,
    int? PageSize,
    int? TotalPages,
    int? TotalRecords);

/// <summary>One record on the wire. <see cref="LastUpdated"/> stays text so a bad value can be reported precisely.</summary>
internal sealed record RecordDto(
    int? Id,
    string? Name,
    string? Category,
    decimal? Price,
    int? Quantity,
    string? LastUpdated);
