using WurmMiningCounter.MiningStatistics;

namespace WurmMiningCounter.Tests.MiningStatistics;

public sealed class NumericSkillUpdateParserTests
{
    [Fact]
    public void Parse_IncreasedMiningCreatesNumericUpdate()
    {
        var update = Parse("[12:34:56] Mining increased by 0,1234 to 10,5678");
        Assert.Equal(new TimeOnly(12, 34, 56), update.Timestamp);
        Assert.Equal("Mining", update.SkillName);
        Assert.Equal(NumericSkillUpdateDirection.Increased, update.Direction);
        Assert.Equal(0.1234m, update.Delta);
        Assert.Equal(0.1234m, update.SignedDelta);
        Assert.Equal(10.5678m, update.CurrentValue);
    }

    [Fact]
    public void Parse_DecreasedMiningCreatesNegativeSignedDelta()
    {
        var update = Parse("[12:34:56] Mining decreased by 0,123456 to 10,444444");
        Assert.Equal(NumericSkillUpdateDirection.Decreased, update.Direction);
        Assert.Equal(0.123456m, update.Delta);
        Assert.Equal(-0.123456m, update.SignedDelta);
        Assert.Equal(10.444444m, update.CurrentValue);
    }

    [Theory]
    [InlineData("[01:02:03] Mining increased by 0,1234 to 1,2345", 0.1234, 1.2345)]
    [InlineData("[01:02:03] Mining increased by 0,123456 to 1,234567", 0.123456, 1.234567)]
    public void Parse_SupportsConfirmedFourAndSixDecimalPlaces(string line, decimal delta, decimal currentValue)
    {
        var update = Parse(line);
        Assert.Equal(delta, update.Delta);
        Assert.Equal(currentValue, update.CurrentValue);
    }

    [Fact]
    public void Parse_AllowsNegativeCurrentValueAndSkillNamesWithSpaces()
    {
        var update = Parse("[01:02:03] Body control decreased by 0,1250 to -1,5000");
        Assert.Equal("Body control", update.SkillName);
        Assert.Equal(-1.5m, update.CurrentValue);
    }

    [Fact]
    public void Parse_AcceptsCompleteLineWithoutTrailingCharacters() => Assert.NotNull(NumericSkillUpdateParser.Parse("[01:02:03] Mining increased by 0,1234 to 1,2345"));

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Parse_RejectsTrailingLineTerminators(string terminator) => Assert.Null(NumericSkillUpdateParser.Parse($"[01:02:03] Mining increased by 0,1234 to 1,2345{terminator}"));

    [Theory]
    [InlineData("[12:34:56] Mining has increased to rank 1 affinity")]
    [InlineData("[12:34:56] Mining increased by 0,1234 to 10,5678 trailing")]
    [InlineData("Mining increased by 0,1234 to 10,5678")]
    [InlineData("[12:34:56] Mining increased by 0.1234 to 10,5678")]
    [InlineData("[12:34:56] Mining increased by 0,123 to 10,5678")]
    [InlineData("[24:00:00] Mining increased by 0,1234 to 10,5678")]
    public void Parse_RejectsAffinityMalformedOrPartialLines(string line) => Assert.Null(NumericSkillUpdateParser.Parse(line));

    private static NumericSkillUpdate Parse(string line) => Assert.IsType<NumericSkillUpdate>(NumericSkillUpdateParser.Parse(line));
}
