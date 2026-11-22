using System.Globalization;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class ProspectingLineParserTests
{
    [Fact]
    public void IsProspectingStart_RecognizesExactStartMessage()
    {
        var result = ProspectingLineParser.IsProspectingStart("You start to gather fragments of the rock.");

        Assert.True(result);
    }

    [Fact]
    public void IsProspectingStart_DoesNotRecognizeQueueAnnouncement()
    {
        var result = ProspectingLineParser.IsProspectingStart("After you finish mining you will start prospecting.");

        Assert.False(result);
    }

    [Theory]
    [InlineData("You stop prospecting.")]
    [InlineData("[12:00:00] You stop prospecting.")]
    public void IsProspectingStop_RecognizesExactMessage(string text)
    {
        var result = ProspectingLineParser.IsProspectingStop(text);

        Assert.True(result);
    }

    [Theory]
    [InlineData("You stop prospecting")]
    [InlineData("you stop prospecting.")]
    [InlineData("You stop prospecting. now")]
    [InlineData("Some unrelated message.")]
    public void IsProspectingStop_DoesNotRecognizeOtherMessages(string text)
    {
        var result = ProspectingLineParser.IsProspectingStop(text);

        Assert.False(result);
    }

    [Theory]
    [MemberData(nameof(ConfirmedMaterialMessages))]
    public void TryParseMaterial_RecognizesConfirmedMaterials(string text, VeinMaterial expectedMaterial)
    {
        var parsed = ProspectingLineParser.TryParseMaterial(text, out var material);

        Assert.True(parsed);
        Assert.Equal(expectedMaterial, material);
    }

    [Theory]
    [MemberData(nameof(UnconfirmedOrUnknownMaterialMessages))]
    public void TryParseMaterial_DoesNotRecognizeUnconfirmedOrUnknownMaterials(string text)
    {
        var parsed = ProspectingLineParser.TryParseMaterial(text, out _);

        Assert.False(parsed);
    }

    [Fact]
    public void TryParseMaterial_RecognizesSaltRockSaltAliasWithoutTreatingItAsPlainRock()
    {
        const string text = "You would mine salt rock salt here.";

        var parsed = ProspectingLineParser.TryParseMaterial(text, out var material);

        Assert.True(parsed);
        Assert.Equal(VeinMaterial.RockSalt, material);
        Assert.False(ProspectingLineParser.IsPlainRock(text));
    }

    [Theory]
    [InlineData("You would mine stone shards here.")]
    [InlineData("You would mine stone stone shards here.")]
    [InlineData("You would mine stone rock shards here.")]
    [InlineData("You find only rock.")]
    public void IsPlainRock_RecognizesExactMessage(string text)
    {
        var result = ProspectingLineParser.IsPlainRock(text);

        Assert.True(result);
    }

    [Theory]
    [InlineData("[12:00:00] You would mine stone shards here.")]
    [InlineData("[12:00:00] You would mine stone stone shards here.")]
    [InlineData("[12:00:00] You would mine stone rock shards here.")]
    [InlineData("[12:00:00] You find only rock.")]
    public void IsPlainRock_RecognizesTimestampedMessage(string text)
    {
        var result = ProspectingLineParser.IsPlainRock(text);

        Assert.True(result);
    }

    [Theory]
    [InlineData("You mine some stone shards.")]
    [InlineData("You would mine zinc ore here.")]
    [InlineData("You would mine stone shards here")]
    [InlineData("you would mine stone shards here.")]
    [InlineData("You would mine stone stone shards here")]
    [InlineData("you would mine stone stone shards here.")]
    [InlineData("You would mine stone stone shard here.")]
    [InlineData("You would mine stone stone stone shards here.")]
    [InlineData("You find only rock")]
    [InlineData("you find only rock.")]
    [InlineData("You find only rocks.")]
    [InlineData("You find only rock here.")]
    [InlineData("Some unrelated message.")]
    public void IsPlainRock_DoesNotRecognizeOtherMessages(string text)
    {
        var result = ProspectingLineParser.IsPlainRock(text);

        Assert.False(result);
    }

    [Theory]
    [InlineData("You would mine stone shards here.")]
    [InlineData("You would mine stone stone shards here.")]
    [InlineData("You would mine stone rock shards here.")]
    public void TryParseMaterial_DoesNotRecognizePlainRock(string text)
    {
        var parsed = ProspectingLineParser.TryParseMaterial(text, out _);

        Assert.False(parsed);
    }

    [Theory]
    [InlineData("You will find salt here!", ProspectingAugment.Salt)]
    [InlineData("You will find flint here!", ProspectingAugment.Flint)]
    [InlineData("[12:00:00] You will find salt here!", ProspectingAugment.Salt)]
    public void TryParseAugment_RecognizesConfirmedMessages(string text, ProspectingAugment expectedAugment)
    {
        var parsed = ProspectingLineParser.TryParseAugment(text, out var augment);

        Assert.True(parsed);
        Assert.Equal(expectedAugment, augment);
    }

    [Theory]
    [InlineData("You mine some salt.")]
    [InlineData("You find flint!")]
    [InlineData("Some unrelated message.")]
    [InlineData("You will find salt here.")]
    [InlineData("you will find flint here!")]
    public void TryParseAugment_DoesNotRecognizeOtherMessages(string text)
    {
        var parsed = ProspectingLineParser.TryParseAugment(text, out _);

        Assert.False(parsed);
    }

    [Theory]
    [MemberData(nameof(ConfirmedNearbyScanMessages))]
    public void TryParseNearbyScan_RecognizesConfirmedOuterGrammar(string text, string expectedDescription)
    {
        var parsed = ProspectingLineParser.TryParseNearbyScan(text, out var observation);

        Assert.True(parsed);
        Assert.Equal(expectedDescription, observation.RawDescription);
    }

    [Fact]
    public void TryParseNearbyScan_ParsesOrderedEntriesWithOptionalGradesAndDuplicates()
    {
        var parsed = ProspectingLineParser.TryParseNearbyScan(
            "There is iron (normal quality), iron (good quality), rock salt, sandstone (poor quality), tin, iron (very good quality), silver (acceptable quality) and gold nearby.",
            out var observation);

        Assert.True(parsed);
        Assert.False(observation.IsOnlyRock);
        Assert.Equal(8, observation.Entries.Count);
        Assert.Equal(
            [
                new NearbyProspectingEntry("iron", "normal"),
                new NearbyProspectingEntry("iron", "good"),
                new NearbyProspectingEntry("rock salt", null),
                new NearbyProspectingEntry("sandstone", "poor"),
                new NearbyProspectingEntry("tin", null),
                new NearbyProspectingEntry("iron", "very good"),
                new NearbyProspectingEntry("silver", "acceptable"),
                new NearbyProspectingEntry("gold", null)
            ],
            observation.Entries);
    }

    [Fact]
    public void TryParseNearbyScan_ParsesOnlyRock()
    {
        var parsed = ProspectingLineParser.TryParseNearbyScan(
            "There is only rock nearby.",
            out var observation);

        Assert.True(parsed);
        Assert.True(observation.IsOnlyRock);
        Assert.Empty(observation.Entries);
        Assert.Equal("only rock", observation.RawDescription);
    }

    [Theory]
    [InlineData("There is  nearby.")]
    [InlineData("There is nearby.")]
    [InlineData("There is tin (poor quality)")]
    [InlineData("There are tin (poor quality) nearby.")]
    [InlineData("there is tin (poor quality) nearby.")]
    [InlineData("You would mine iron ore here.")]
    [InlineData("Some unrelated message.")]
    public void TryParseNearbyScan_DoesNotRecognizeInvalidOuterGrammar(string text)
    {
        var parsed = ProspectingLineParser.TryParseNearbyScan(text, out _);

        Assert.False(parsed);
    }

    [Fact]
    public void TryParseExactMaxQuality_ParsesInvariantCultureDecimal()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");

            var parsed = ProspectingLineParser.TryParseExactMaxQuality(
                "It has a max quality of 12.5.",
                out var quality);

            Assert.True(parsed);
            Assert.Equal(12.5m, quality.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void TryParseExactRemaining_ParsesInteger()
    {
        var parsed = ProspectingLineParser.TryParseExactRemaining(
            "You will be able to mine here 1103 more times.",
            out var remaining);

        Assert.True(parsed);
        Assert.Equal(1103, remaining.Value);
    }

    [Theory]
    [MemberData(nameof(RemainingBandMessages))]
    public void TryParseEstimateBand_RecognizesExactCaseSensitiveMessages(
        string text,
        ProspectingRemainingBand expectedBand)
    {
        var parsed = ProspectingLineParser.TryParseEstimateBand(text, out var remaining);

        Assert.True(parsed);
        Assert.Equal(expectedBand, remaining.Value);
    }

    [Theory]
    [InlineData("Only a few weeks mining remain here.")]
    [InlineData("[12:00:00] Only a few weeks mining remain here.")]
    public void TryParseEstimateBand_RecognizesTimestampNormalizedMessage(string text)
    {
        var parsed = ProspectingLineParser.TryParseEstimateBand(text, out var remaining);

        Assert.True(parsed);
        Assert.Equal(ProspectingRemainingBand.FewWeeks, remaining.Value);
    }

    [Theory]
    [InlineData("There is Plenty of ore left here.")]
    [InlineData("only a few weeks mining remain here.")]
    [InlineData("The ore will run out soon")]
    [InlineData("You will be able to mine here 1103 more times.")]
    [InlineData("Some unrelated message.")]
    public void TryParseEstimateBand_DoesNotRecognizeOtherMessages(string text)
    {
        var parsed = ProspectingLineParser.TryParseEstimateBand(text, out _);

        Assert.False(parsed);
    }

    [Theory]
    [MemberData(nameof(QualityGradeMessages))]
    public void TryParseQualityGrade_RecognizesExactCaseSensitiveMessages(
        string text,
        ProspectingQualityGrade expectedGrade)
    {
        var parsed = ProspectingLineParser.TryParseQualityGrade(text, out var quality);

        Assert.True(parsed);
        Assert.Equal(expectedGrade, quality.Value);
    }

    [Theory]
    [InlineData("It is of good quality.")]
    [InlineData("[12:00:00] It is of good quality.")]
    public void TryParseQualityGrade_RecognizesTimestampNormalizedMessage(string text)
    {
        var parsed = ProspectingLineParser.TryParseQualityGrade(text, out var quality);

        Assert.True(parsed);
        Assert.Equal(ProspectingQualityGrade.Good, quality.Value);
    }

    [Theory]
    [InlineData("It is of Good quality.")]
    [InlineData("it is of good quality.")]
    [InlineData("It is of good quality")]
    [InlineData("It has a max quality of 12.5.")]
    [InlineData("Some unrelated message.")]
    public void TryParseQualityGrade_DoesNotRecognizeOtherMessages(string text)
    {
        var parsed = ProspectingLineParser.TryParseQualityGrade(text, out _);

        Assert.False(parsed);
    }

    [Theory]
    [InlineData("It has a max quality of twelve.")]
    [InlineData("It has a max quality of 12.5")]
    [InlineData("Some unrelated message.")]
    public void TryParseExactMaxQuality_DoesNotRecognizeMalformedOrUnrelatedMessages(string text)
    {
        var parsed = ProspectingLineParser.TryParseExactMaxQuality(text, out _);

        Assert.False(parsed);
    }

    [Theory]
    [InlineData("You will be able to mine here many more times.")]
    [InlineData("You will be able to mine here 12 more times")]
    [InlineData("You will be able to mine here 12.5 more times.")]
    [InlineData("Some unrelated message.")]
    public void TryParseExactRemaining_DoesNotRecognizeMalformedOrUnrelatedMessages(string text)
    {
        var parsed = ProspectingLineParser.TryParseExactRemaining(text, out _);

        Assert.False(parsed);
    }

    public static IEnumerable<object[]> ConfirmedMaterialMessages =>
    [
        ["You would mine iron ore here.", VeinMaterial.Iron],
        ["You would mine copper ore here.", VeinMaterial.Copper],
        ["You would mine tin ore here.", VeinMaterial.Tin],
        ["You would mine zinc ore here.", VeinMaterial.Zinc],
        ["You would mine lead ore here.", VeinMaterial.Lead],
        ["You would mine silver ore here.", VeinMaterial.Silver],
        ["You would mine gold ore here.", VeinMaterial.Gold],
        ["You would mine adamantine ore here.", VeinMaterial.Adamantine],
        ["You would mine glimmersteel ore here.", VeinMaterial.Glimmersteel],
        ["You would mine sandstone shards here.", VeinMaterial.Sandstone],
        ["You would mine marble shards here.", VeinMaterial.Marble],
        ["You would mine slate shards here.", VeinMaterial.Slate],
        ["You would mine rock salt here.", VeinMaterial.RockSalt],
        ["You would mine salt rock salt here.", VeinMaterial.RockSalt]
    ];

    public static IEnumerable<object[]> QualityGradeMessages =>
    [
        ["It is of poor quality.", ProspectingQualityGrade.Poor],
        ["It is of acceptable quality.", ProspectingQualityGrade.Acceptable],
        ["It is of normal quality.", ProspectingQualityGrade.Normal],
        ["It is of good quality.", ProspectingQualityGrade.Good],
        ["It is of very good quality.", ProspectingQualityGrade.VeryGood],
        ["It is of utmost quality.", ProspectingQualityGrade.Utmost]
    ];

    public static IEnumerable<object[]> RemainingBandMessages =>
    [
        ["There is plenty of ore left here.", ProspectingRemainingBand.Plenty],
        ["Only a few weeks mining remain here.", ProspectingRemainingBand.FewWeeks],
        ["The ore is starting to deplete.", ProspectingRemainingBand.StartingToDeplete],
        ["You should start to prospect for another vein of this ore.", ProspectingRemainingBand.ProspectAnotherVein],
        ["The ore will run out soon.", ProspectingRemainingBand.RunOutSoon],
        ["The ore will run out any hour.", ProspectingRemainingBand.RunOutAnyHour]
    ];

    public static IEnumerable<object[]> ConfirmedNearbyScanMessages =>
    [
        ["There is tin (poor quality) nearby.", "tin (poor quality)"],
        ["There is iron (normal quality) and iron (good quality) nearby.", "iron (normal quality) and iron (good quality)"],
        ["There is iron (normal quality), iron (good quality), iron (poor quality) and sandstone (normal quality) nearby.", "iron (normal quality), iron (good quality), iron (poor quality) and sandstone (normal quality)"],
        ["There is iron (normal quality), silver (acceptable quality) and iron (very good quality) nearby.", "iron (normal quality), silver (acceptable quality) and iron (very good quality)"],
        ["There is rock salt (good quality), tin (acceptable quality), rock salt (very good quality), sandstone (poor quality), iron (good quality) and sandstone (very good quality) nearby.", "rock salt (good quality), tin (acceptable quality), rock salt (very good quality), sandstone (poor quality), iron (good quality) and sandstone (very good quality)"],
        ["[12:00:00] There is tin (poor quality) nearby.", "tin (poor quality)"]
    ];

    public static IEnumerable<object[]> UnconfirmedOrUnknownMaterialMessages =>
    [
        ["You would mine iron here."],
        ["You would mine copper here."],
        ["You would mine tin here."],
        ["You would mine zinc here."],
        ["You would mine lead here."],
        ["You would mine sandstone here."],
        ["You would mine marble here."],
        ["You would mine gold here."],
        ["You would mine salty rock salt here."],
        ["You would mine adamantine here."]
    ];
}
