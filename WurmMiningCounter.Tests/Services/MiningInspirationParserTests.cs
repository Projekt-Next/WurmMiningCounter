using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class MiningInspirationParserTests
{
    [Theory]
    [InlineData("You have a moment of inspiration...")]
    [InlineData("[12:34:56] You have a moment of inspiration...")]
    public void IsMomentOfInspiration_RecognizesExactNormalizedMessage(string line)
    {
        Assert.True(MiningInspirationParser.IsMomentOfInspiration(line));
    }

    [Theory]
    [InlineData("You have a moment of inspiration")]
    [InlineData("You have a moment of inspiration....")]
    [InlineData("Someone has a moment of inspiration...")]
    public void IsMomentOfInspiration_RejectsOtherMessages(string line)
    {
        Assert.False(MiningInspirationParser.IsMomentOfInspiration(line));
    }
}
