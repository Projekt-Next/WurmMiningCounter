using WurmMiningCounter.Models;

namespace WurmMiningCounter.Tests.Models;

public sealed class EventLogMessagesTests
{
    [Fact]
    public void WallBreakMessagesRemainExact()
    {
        Assert.Equal("The wall will break soon.", EventLogMessages.WallBreakSoon);
        Assert.Equal("The wall breaks!", EventLogMessages.WallBreak);
    }
}
