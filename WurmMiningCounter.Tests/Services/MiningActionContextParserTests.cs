using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class MiningActionContextParserTests
{
    [Theory]
    [InlineData("You start to mine forwards.", MiningActionContext.Wall)]
    [InlineData("You start to mine upwards.", MiningActionContext.Wall)]
    [InlineData("You start to mine up.", MiningActionContext.Wall)]
    [InlineData("You start to mine downwards.", MiningActionContext.Wall)]
    [InlineData("You start to mine down.", MiningActionContext.Wall)]
    [InlineData("You start to mine the floor.", MiningActionContext.Floor)]
    [InlineData("You start to mine the ceiling.", MiningActionContext.Ceiling)]
    [InlineData("You start to mine.", MiningActionContext.Surface)]
    [InlineData("You start to tunnel.", MiningActionContext.Tunnelling)]
    public void Parse_RecognizesConfirmedStartMessage(string line, MiningActionContext expected)
    {
        var result = new MiningActionContextParser().Parse(line);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("[12:00:00] You start to mine forwards.", MiningActionContext.Wall)]
    [InlineData("[12:00:01] You start to mine.", MiningActionContext.Surface)]
    [InlineData("[12:00:02] You start to tunnel.", MiningActionContext.Tunnelling)]
    public void Parse_RecognizesTimestampedStartMessage(string line, MiningActionContext expected)
    {
        var result = new MiningActionContextParser().Parse(line);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("After you finish mining you will start mining again.")]
    [InlineData("After you finish mining you will start prospecting.")]
    [InlineData("After you finish analysing you will start mining.")]
    [InlineData("[12:00:00] After you finish mining you will start mining again.")]
    [InlineData("You start to mine the wall in front of you.")]
    [InlineData("You start to mine the surface.")]
    [InlineData("You start tunnelling.")]
    [InlineData("You Start to mine forwards.")]
    [InlineData("You start to mine forwards")]
    [InlineData("You start to mine forwards. Now.")]
    [InlineData("Some unrelated message.")]
    public void Parse_DoesNotRecognizeUnsupportedMessages(string line)
    {
        var result = new MiningActionContextParser().Parse(line);

        Assert.Null(result);
    }
}
