using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventLogSegmentOrderTests
{
    [Theory]
    [InlineData("_Event.2026-08.txt", "Monthly")]
    [InlineData("_Event.2026-08-23.txt", "Daily")]
    [InlineData("_Event.2026-08.TXT", "Monthly")]
    [InlineData("_Event.2026-08-23.Txt", "Daily")]
    [InlineData("_Event.invalid.txt", "Unknown")]
    public void Parse_ClassifiesKnownFileNameFormats(string fileName, string expectedFormat)
    {
        var segment = EventLogSegmentOrder.Parse($"C:\\logs\\{fileName}");

        Assert.Equal(expectedFormat, segment.Format.ToString());
    }

}
