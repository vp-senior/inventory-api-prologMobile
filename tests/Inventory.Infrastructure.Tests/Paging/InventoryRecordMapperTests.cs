using Inventory.Infrastructure.Http;
using Inventory.Infrastructure.Paging;
using Inventory.Infrastructure.Tests.Support;

namespace Inventory.Infrastructure.Tests.Paging;

public class InventoryRecordMapperTests
{
    // Keyed by a description of the defect; the value is the problem text it must produce.
    private static readonly Dictionary<string, (RecordDto Dto, string ExpectedProblem)> ContractViolations = new()
    {
        ["id missing"] = (Wire.Record(1) with { Id = null }, "no id"),
        ["name missing"] = (Wire.Record(1) with { Name = null }, "no name"),
        ["name blank"] = (Wire.Record(1) with { Name = "   " }, "no name"),
        ["category missing"] = (Wire.Record(1) with { Category = null }, "no category"),
        ["category empty"] = (Wire.Record(1) with { Category = "" }, "no category"),
        ["price missing"] = (Wire.Record(1) with { Price = null }, "invalid price"),
        ["price negative"] = (Wire.Record(1) with { Price = -0.01m }, "invalid price"),
        ["quantity missing"] = (Wire.Record(1) with { Quantity = null }, "invalid quantity"),
        ["quantity negative"] = (Wire.Record(1) with { Quantity = -1 }, "invalid quantity"),
        ["lastUpdated missing"] = (Wire.Record(1) with { LastUpdated = null }, "invalid lastUpdated"),
        ["lastUpdated not a date"] = (Wire.Record(1) with { LastUpdated = "yesterday" }, "invalid lastUpdated"),
        ["lastUpdated not ISO 8601"] = (Wire.Record(1) with { LastUpdated = "10/15/2026 12:30" }, "invalid lastUpdated"),
        ["lastUpdated impossible date"] = (Wire.Record(1) with { LastUpdated = "2026-13-45T00:00:00Z" }, "invalid lastUpdated"),
    };

    public static TheoryData<string> ContractViolationNames => [.. ContractViolations.Keys];

    [Theory]
    [MemberData(nameof(ContractViolationNames))]
    public void A_record_that_breaks_the_contract_is_not_mapped_and_the_reason_is_reported(string defect)
    {
        var (dto, expectedProblem) = ContractViolations[defect];
        var problems = new List<string>();

        var record = InventoryRecordMapper.Map(dto, position: 4, problems);

        Assert.Null(record);
        Assert.Contains(expectedProblem, Assert.Single(problems));
    }

    [Fact]
    public void A_null_entry_in_the_records_array_is_reported_by_position()
    {
        var problems = new List<string>();

        var record = InventoryRecordMapper.Map(null, position: 4, problems);

        Assert.Null(record);
        Assert.Equal("Record #4 is null.", Assert.Single(problems));
    }

    [Fact]
    public void Every_problem_with_a_record_is_reported_not_just_the_first()
    {
        var problems = new List<string>();

        InventoryRecordMapper.Map(new RecordDto(7, null, null, -1m, -1, "nope"), position: 1, problems);

        Assert.Equal(5, problems.Count);
        Assert.All(problems, problem => Assert.StartsWith("Record 7", problem));
    }

    [Fact]
    public void Zero_quantity_and_zero_price_are_valid()
    {
        var problems = new List<string>();

        var record = InventoryRecordMapper.Map(Wire.Record(1, price: 0m, quantity: 0), position: 1, problems);

        Assert.NotNull(record);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("2026-10-15T12:30:00Z", "2026-10-15T12:30:00+00:00")]
    [InlineData("2026-10-15T12:30:00.123Z", "2026-10-15T12:30:00.123+00:00")]
    [InlineData("2026-10-15T12:30:00+00:00", "2026-10-15T12:30:00+00:00")]
    [InlineData("2026-10-15T14:30:00+02:00", "2026-10-15T12:30:00+00:00")] // offset: same instant, in UTC
    [InlineData("2026-10-15T12:30:00", "2026-10-15T12:30:00+00:00")] // no offset: UTC, as the contract states
    public void Timestamps_are_read_as_iso_8601_and_normalised_to_utc(string text, string expected)
    {
        Assert.True(InventoryRecordMapper.TryParseTimestamp(text, out var value));
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
        Assert.Equal(TimeSpan.Zero, value.Offset);
    }
}
