using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class MiningEventParserTests
{
    [Theory]
    [MemberData(nameof(ValidMessages))]
    public void Parse_RecognizesConfirmedMessage(string line, MiningMaterial expectedMaterial)
    {
        var result = new MiningEventParser().Parse(line);

        Assert.NotNull(result);
        Assert.Equal(expectedMaterial, result.Material);
    }

    [Theory]
    [MemberData(nameof(TimestampedMessages))]
    public void Parse_RecognizesTimestampedConfirmedMessage(string line, MiningMaterial expectedMaterial)
    {
        var result = new MiningEventParser().Parse(line);

        Assert.NotNull(result);
        Assert.Equal(expectedMaterial, result.Material);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t")]
    public void Parse_ReturnsNullForNullOrWhitespace(string? line)
    {
        var result = new MiningEventParser().Parse(line!);

        Assert.Null(result);
    }

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public void Parse_DoesNotRecognizeOtherMessages(string line)
    {
        var result = new MiningEventParser().Parse(line);

        Assert.Null(result);
    }

    public static IEnumerable<object[]> ValidMessages =>
    [
        ["You mine some stone shards.", MiningMaterial.StoneShards],
        ["You mine some ore.", MiningMaterial.Ore],
        ["You mine some shards.", MiningMaterial.Shards],
        ["You mine some rock salt.", MiningMaterial.RockSalt],
        ["You mine some pink crystals.", MiningMaterial.SourceCrystal],
        ["You mine some salt.", MiningMaterial.Salt],
        ["You find flint!", MiningMaterial.Flint],
        ["You get a source crystal.", MiningMaterial.SourceCrystal],
        ["You find a sapphire!", MiningMaterial.Sapphire],
        ["You find a diamond!", MiningMaterial.Diamond],
        ["You find an emerald!", MiningMaterial.Emerald],
        ["You find a ruby!", MiningMaterial.Ruby],
        ["You find an opal!", MiningMaterial.Opal]
    ];

    public static IEnumerable<object[]> TimestampedMessages =>
    [
        ["[06:22:08] You mine some stone shards.", MiningMaterial.StoneShards],
        ["[00:00:01] You mine some ore.", MiningMaterial.Ore],
        ["[23:59:59] You find an opal!", MiningMaterial.Opal],
        ["[12:34:55] You mine some pink crystals.", MiningMaterial.SourceCrystal],
        ["[12:34:56] You get a source crystal.", MiningMaterial.SourceCrystal],
        ["[00:00:00] You mine some ore.", MiningMaterial.Ore],
        ["[23:59:59] You mine some ore.", MiningMaterial.Ore]
    ];

    public static IEnumerable<object[]> InvalidMessages =>
    [
        ["ProfileB mines some stone shards."],
        ["You start to mine the ceiling."],
        ["After you finish mining you will start mining again."],
        ["You chip away at the rock."],
        ["You cannot reach the ceiling."],
        ["There is no space to mine here. Clear the area first."],
        ["The shards doesn't fit."],
        ["The stone shards doesn't fit."],
        ["You mine some mithril."],
        ["You find an amethyst!"],
        ["Player gets a source crystal."],
        ["You get 18 source crystals."],
        ["you mine some stone shards."],
        ["Before You mine some stone shards."],
        ["You mine some stone shards. After"],
        ["[6:22:08] You mine some stone shards."],
        ["[24:00:00] You mine some ore."],
        ["[99:99:99] You mine some ore."],
        ["[12:60:00] You mine some ore."],
        ["[12:00:60] You mine some ore."],
        ["[1:00:00] You mine some ore."],
        ["[01:1:00] You mine some ore."],
        ["[01:00:1] You mine some ore."],
        ["[01-00-00] You mine some ore."],
        ["[01:00:00]You mine some ore."],
        ["[01:00:00]  You mine some ore."],
        ["[٠١:٠٠:٠٠] You mine some ore."]
    ];
}
