using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class SessionBoundaryParserTests
{
    [Theory]
    [InlineData("Logging started 2026-08-21")]
    [InlineData("[23:59:59] Logging started 2026-08-21")]
    public void IsLoggingStarted_RecognizesValidCanonicalMessage(string line)
    {
        var result = new SessionBoundaryParser().IsLoggingStarted(line);

        Assert.True(result);
    }

    [Theory]
    [InlineData("Logging started")]
    [InlineData("Logging started 2026/08/21")]
    [InlineData("Logging started 2026-8-21")]
    [InlineData("Logging started 2026-02-29")]
    [InlineData("Logging started 2026-08-21 extra")]
    [InlineData("Before Logging started 2026-08-21")]
    [InlineData("logging started")]
    [InlineData("Some unrelated message.")]
    public void IsLoggingStarted_DoesNotRecognizeInvalidOrOtherMessages(string line)
    {
        var result = new SessionBoundaryParser().IsLoggingStarted(line);

        Assert.False(result);
    }

    [Theory]
    [InlineData("Reconnecting Alice! Independence.")]
    [InlineData("[23:59:59] Reconnecting Alice! Independence.")]
    public void IsReconnect_RecognizesValidCanonicalMessage(string line)
    {
        var result = new SessionBoundaryParser().IsReconnect(line);

        Assert.True(result);
    }

    [Theory]
    [InlineData("Reconnecting ! Independence.")]
    [InlineData("Reconnecting Alice! .")]
    [InlineData("Reconnecting Alice! Independence")]
    [InlineData("Reconnecting Alice! Independence! Server.")]
    [InlineData("Reconnecting Alice!Independence.")]
    [InlineData("reconnecting Alice! Independence.")]
    [InlineData("Some unrelated message.")]
    public void IsReconnect_DoesNotRecognizeInvalidOrOtherMessages(string line)
    {
        var result = new SessionBoundaryParser().IsReconnect(line);

        Assert.False(result);
    }
}
