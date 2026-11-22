using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class MiningAttemptTransitionParserTests
{
    [Theory]
    [InlineData("The wall breaks!", MiningAttemptTransitionKind.WallBreak)]
    [InlineData("The wall will break soon.", MiningAttemptTransitionKind.WallBreakSoon)]
    [InlineData("A deep shaft will probably emerge.", MiningAttemptTransitionKind.InformationalWarning)]
    [InlineData("You stop mining.", MiningAttemptTransitionKind.NoYieldTerminal)]
    [InlineData("You chip away at the rock.", MiningAttemptTransitionKind.ContextSensitiveNoYield)]
    [InlineData("You realize you can't open a tunnel here, because the surface is too flat.", MiningAttemptTransitionKind.ContextSensitiveNoYield)]
    [InlineData("You're too busy.", MiningAttemptTransitionKind.PreStartRejection)]
    public void Parse_RecognizesExactTransitionMessage(
        string line,
        MiningAttemptTransitionKind expected)
    {
        var result = MiningAttemptTransitionParser.Parse(line);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("[12:00:00] The wall breaks!", MiningAttemptTransitionKind.WallBreak)]
    [InlineData("[12:00:01] The wall will break soon.", MiningAttemptTransitionKind.WallBreakSoon)]
    [InlineData("[12:00:02] You stop mining.", MiningAttemptTransitionKind.NoYieldTerminal)]
    [InlineData("[12:00:03] You chip away at the rock.", MiningAttemptTransitionKind.ContextSensitiveNoYield)]
    [InlineData("[12:00:04] You're too busy.", MiningAttemptTransitionKind.PreStartRejection)]
    public void Parse_RecognizesTimestampedTransitionMessage(
        string line,
        MiningAttemptTransitionKind expected)
    {
        var result = MiningAttemptTransitionParser.Parse(line);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("the wall breaks!")]
    [InlineData("The wall breaks")]
    [InlineData("The wall breaks! Now.")]
    [InlineData("The wall collapsed!")]
    [InlineData("Some unrelated message.")]
    public void Parse_ReturnsNullForNonExactMessage(string line)
    {
        var result = MiningAttemptTransitionParser.Parse(line);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("The cave walls echo strangely...")]
    [InlineData("too far")]
    [InlineData("must use mining tool")]
    [InlineData("You cannot reach the ceiling.")]
    [InlineData("There is no space to mine here. Clear the area first.")]
    [InlineData("The rock is too hard to mine.")]
    public void Parse_DoesNotGeneralizeUnsupportedFailureFamilies(string line)
    {
        var result = MiningAttemptTransitionParser.Parse(line);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("You start to mine forwards.")]
    [InlineData("You mine some ore.")]
    [InlineData("You start to gather fragments of the rock.")]
    [InlineData("You start to analyse the shard.")]
    [InlineData("There is tin (poor quality) nearby.")]
    [InlineData("A vein of sand-sized minerals or rock grains, composed of quartz or feldspar, because these are the most common minerals in the Wurm's crust.")]
    public void Parse_DoesNotRecognizeOtherDomainMessages(string line)
    {
        var result = MiningAttemptTransitionParser.Parse(line);

        Assert.Null(result);
    }
}
