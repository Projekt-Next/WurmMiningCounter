using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventLogLineNormalizerTests
{
    [Theory]
    [InlineData("[00:00:00] Event message", "Event message")]
    [InlineData("[23:59:59] Event message", "Event message")]
    public void Normalize_RemovesValidTimestampPrefix(string line, string expected)
    {
        var result = EventLogLineNormalizer.Normalize(line);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Event message")]
    [InlineData("Logging started 2026-08-21")]
    [InlineData("[24:00:00] Event message")]
    [InlineData("[12:60:00] Event message")]
    [InlineData("[12:00:60] Event message")]
    [InlineData("(12:00:00) Event message")]
    [InlineData("[12-00-00] Event message")]
    [InlineData("[12:00:00]Event message")]
    [InlineData("[ab:00:00] Event message")]
    [InlineData("[12:cd:00] Event message")]
    [InlineData("[12:00:ef] Event message")]
    public void Normalize_LeavesNonTimestampOrInvalidPrefixUnchanged(string line)
    {
        var result = EventLogLineNormalizer.Normalize(line);

        Assert.Equal(line, result);
    }

    [Fact]
    public void Normalize_RemovesOnlyTheValidPrefixSpace()
    {
        var result = EventLogLineNormalizer.Normalize("[12:00:00]  Event message");

        Assert.Equal(" Event message", result);
    }
}
