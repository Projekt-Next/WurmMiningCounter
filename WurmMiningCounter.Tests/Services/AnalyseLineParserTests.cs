using WurmMiningCounter.Services;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Tests.Services;

public sealed class AnalyseLineParserTests
{
    [Theory]
    [InlineData("You start to analyse the shard.", AnalyseInput.Shard)]
    [InlineData("[12:00:00] You start to analyse the ore.", AnalyseInput.Ore)]
    public void Start_IdentifiesInput(string text, AnalyseInput expected)
    {
        Assert.True(AnalyseLineParser.TryParseStart(text, out var input));
        Assert.Equal(expected, input);
    }

    [Theory]
    [InlineData("You finish analysing the shard.", AnalyseInput.Shard)]
    [InlineData("[12:00:00] You finish analysing the ore.", AnalyseInput.Ore)]
    public void Finish_IdentifiesInput(string text, AnalyseInput expected)
    {
        Assert.True(AnalyseLineParser.TryParseFinish(text, out var input));
        Assert.Equal(expected, input);
    }

    [Theory]
    [InlineData("You notice a trace of utmost quality slate (east).", AnalyseStrength.Trace, "slate", "utmost", "east")]
    [InlineData("You see a slight trace of acceptable quality iron (east).", AnalyseStrength.Slight, "iron", "acceptable", "east")]
    [InlineData("You spot a faint trace of utmost quality copper (east of south).", AnalyseStrength.Faint, "copper", "utmost", "east of south")]
    [InlineData("You notice a minuscule trace of poor quality sandstone (north of east).", AnalyseStrength.Minuscule, "sandstone", "poor", "north of east")]
    [InlineData("You see a vague trace of very good quality gold (west of south).", AnalyseStrength.Vague, "gold", "very good", "west of south")]
    [InlineData("[12:00:00] You notice an indistinct trace of something, but cannot quite make it out (south of east).", AnalyseStrength.Indistinct, null, null, "south of east")]
    [InlineData("You notice an indistinct trace of normal quality rock salt (north of east).", AnalyseStrength.Indistinct, "rock salt", "normal", "north of east")]
    [InlineData("You notice a faint trace of salt (north).", AnalyseStrength.Faint, "salt", null, "north")]
    [InlineData("You see a vague trace of flint (northwest).", AnalyseStrength.Vague, "flint", null, "northwest")]
    [InlineData("You spot an indistinct trace of something, but cannot quite make it out (south of east).", AnalyseStrength.Indistinct, null, null, "south of east")]
    [InlineData("[21:13:28] You spot a vague trace of something, but cannot quite make it out (south of west).", AnalyseStrength.Vague, null, null, "south of west")]
    public void Trace_ParsesLogConfirmedExamples(string text, AnalyseStrength strength, string? material, string? quality, string direction)
    {
        Assert.True(AnalyseLineParser.TryParseTrace(text, out var trace));
        Assert.Equal(new AnalyseTrace(strength, material, quality, direction, text), trace);
    }

    [Theory]
    [InlineData("You do not notice any unusual traces.", AnalyseStrength.Trace)]
    [InlineData("You cannot see any unusual traces of anything.", AnalyseStrength.Trace)]
    [InlineData("You do not notice any unusual slight traces.", AnalyseStrength.Slight)]
    [InlineData("You cannot see any unusual slight traces of anything.", AnalyseStrength.Slight)]
    [InlineData("You do not notice any unusual faint traces.", AnalyseStrength.Faint)]
    [InlineData("You cannot see any unusual faint traces of anything.", AnalyseStrength.Faint)]
    [InlineData("You cannot see anything unusual.", null)]
    [InlineData("You do not notice any unusual minuscule traces.", AnalyseStrength.Minuscule)]
    [InlineData("You do not notice any unusual vague traces.", AnalyseStrength.Vague)]
    [InlineData("You cannot see any unusual vague traces of anything.", AnalyseStrength.Vague)]
    [InlineData("You cannot see any unusual indistinct traces of anything.", AnalyseStrength.Indistinct)]
    public void NoTrace_ParsesExactLogConfirmedMessages(string text, AnalyseStrength? strength)
    {
        Assert.True(AnalyseLineParser.TryParseNoTrace("[12:00:00] " + text, out var observation));
        Assert.Equal(strength, observation.Strength);
        Assert.Equal("[12:00:00] " + text, observation.RawText);
    }

    [Theory]
    [InlineData("You take a closer look.", AnalyseStrength.Trace)]
    [InlineData("You study it a bit more.", AnalyseStrength.Slight)]
    [InlineData("You study it real hard.", AnalyseStrength.Faint)]
    [InlineData("You peer at it.", AnalyseStrength.Minuscule)]
    [InlineData("You go cross-eyed studying it.", AnalyseStrength.Vague)]
    public void StageMarker_IdentifiesPrecedingStage(string text, AnalyseStrength strength)
    {
        Assert.True(AnalyseLineParser.TryParseStageMarker(text, out var actual));
        Assert.Equal(strength, actual);
    }

    [Fact]
    public void ParsingRepeatedObservationsPreservesEveryOccurrenceAndOrder()
    {
        string[] lines = ["You notice a trace of utmost quality slate (east).", "You notice a trace of utmost quality slate (east).", "You take a closer look."];
        var parsed = lines.Select(line => AnalyseLineParser.TryParseTrace(line, out var trace) ? trace : null).ToArray();
        Assert.NotNull(parsed[0]);
        Assert.Equal(parsed[0], parsed[1]);
        Assert.Null(parsed[2]);
        Assert.True(AnalyseLineParser.TryParseStageMarker(lines[2], out _));
    }

    [Theory]
    [InlineData("Lots of different sized stone shards.")]
    [InlineData("After you finish analysing you will start mining.")]
    [InlineData("After you finish mining you will start analysing.")]
    [InlineData("There is tin (poor quality) nearby.")]
    [InlineData("You notice a trace of utmost quality slate (up).")]
    [InlineData("You do not notice any unusual indistinct traces.")]
    public void UnrelatedOrUnconfirmedLinesAreNotResults(string text)
    {
        Assert.False(AnalyseLineParser.TryParseTrace(text, out _));
        Assert.False(AnalyseLineParser.TryParseNoTrace(text, out _));
        Assert.False(AnalyseLineParser.TryParseStageMarker(text, out _));
    }

    [Theory]
    [InlineData("[08:26:55] You spot a trace of very good quality copper.", AnalyseStrength.Trace, "copper", "very good")]
    [InlineData("[08:26:58] You see a slight trace of salt.", AnalyseStrength.Slight, "salt", null)]
    [InlineData("[08:26:58] You see a slight trace of normal quality iron.", AnalyseStrength.Slight, "iron", "normal")]
    [InlineData("[08:26:58] You notice a slight trace of poor quality iron.", AnalyseStrength.Slight, "iron", "poor")]
    [InlineData("[08:27:05] You see a minuscule trace of acceptable quality tin.", AnalyseStrength.Minuscule, "tin", "acceptable")]
    [InlineData("[08:27:05] You see a minuscule trace of salt.", AnalyseStrength.Minuscule, "salt", null)]
    public void Trace_ParsesLiveWithoutDirection(string text, AnalyseStrength strength, string material, string? quality)
    {
        Assert.True(AnalyseLineParser.TryParseTrace(text, out var trace));
        Assert.Equal(new AnalyseTrace(strength, material, quality, null, text), trace);
    }
    [Theory]
    [InlineData("You start to analyse the shard.")]
    [InlineData("[12:00:00] You start to analyse the shard.")]
    [InlineData("You start to analyse the ore.")]
    [InlineData("[12:00:00] You start to analyse the ore.")]
    public void IsAnalyseStart_RecognizesExactAndTimestampedMessages(string text)
    {
        var result = AnalyseLineParser.IsAnalyseStart(text);

        Assert.True(result);
    }

    [Theory]
    [InlineData("You start to analyse the shard")]
    [InlineData("you start to analyse the shard.")]
    [InlineData("You start to analyse the shard. now")]
    [InlineData("You start to analyse the rock.")]
    public void IsAnalyseStart_DoesNotRecognizeMalformedMessages(string text)
    {
        var result = AnalyseLineParser.IsAnalyseStart(text);

        Assert.False(result);
    }

    [Theory]
    [InlineData("You finish analysing the shard.")]
    [InlineData("[12:00:01] You finish analysing the shard.")]
    [InlineData("You finish analysing the ore.")]
    [InlineData("[12:00:01] You finish analysing the ore.")]
    public void IsAnalyseFinish_RecognizesExactAndTimestampedMessages(string text)
    {
        var result = AnalyseLineParser.IsAnalyseFinish(text);

        Assert.True(result);
    }

    [Theory]
    [InlineData("You finish analysing the shard")]
    [InlineData("you finish analysing the shard.")]
    [InlineData("You finish analysing the shard. now")]
    [InlineData("You finish analysing the rock.")]
    public void IsAnalyseFinish_DoesNotRecognizeMalformedMessages(string text)
    {
        var result = AnalyseLineParser.IsAnalyseFinish(text);

        Assert.False(result);
    }

    [Theory]
    [InlineData("You start to gather fragments of the rock.")]
    [InlineData("There is tin (poor quality) nearby.")]
    [InlineData("A vein of sand-sized minerals or rock grains, composed of quartz or feldspar, because these are the most common minerals in the Wurm's crust.")]
    [InlineData("You start to mine the floor.")]
    [InlineData("Some unrelated message.")]
    public void AnalyseBoundaries_DoNotRecognizeOtherDomainMessages(string text)
    {
        Assert.False(AnalyseLineParser.IsAnalyseStart(text));
        Assert.False(AnalyseLineParser.IsAnalyseFinish(text));
    }
}
