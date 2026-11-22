using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventLogFileNameMatcherTests
{
    [Theory]
    [InlineData("_Event.2026-08.txt")]
    [InlineData("_Event.2019-10-07.txt")]
    [InlineData("_Event.2027-01.TXT")]
    [InlineData("_Event.9999-12-31.Txt")]
    public void IsMatchAcceptsValidEventLogFileNames(string fileName)
    {
        Assert.True(EventLogFileNameMatcher.IsMatch(fileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Event.2026-08.txt")]
    [InlineData("_Event.2026-8.txt")]
    [InlineData("_Event.2026-08-7.txt")]
    [InlineData("_Event.26-08.txt")]
    [InlineData("_Event.2026-00.txt")]
    [InlineData("_Event.2026-13.txt")]
    [InlineData("_Event.2026-08-00.txt")]
    [InlineData("_Event.2026-08-32.txt")]
    [InlineData("_Event.2026-08.txt.bak")]
    [InlineData("backup_Event.2026-08.txt")]
    [InlineData("_Event.202A-08.txt")]
    [InlineData("_Event.2026-0A.txt")]
    [InlineData("_Event.２０２６-08.txt")]
    public void IsMatchRejectsInvalidEventLogFileNames(string? fileName)
    {
        Assert.False(EventLogFileNameMatcher.IsMatch(fileName));
    }
}
