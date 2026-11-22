using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class ProspectingSequenceAssemblerTests
{
    private const string Start = "You start to gather fragments of the rock.";
    private const string Iron = "You would mine iron ore here.";
    private const string Zinc = "You would mine zinc ore here.";
    private const string Gold = "You would mine gold ore here.";
    private const string PlainRock = "You would mine stone shards here.";
    private const string StoneRockPlainRock = "You would mine stone rock shards here.";
    private const string FindOnlyRock = "You find only rock.";
    private const string RockSalt = "You would mine rock salt here.";
    private const string SaltRockSalt = "You would mine salt rock salt here.";
    private const string Quality = "It has a max quality of 12.5.";
    private const string Remaining = "You will be able to mine here 1103 more times.";
    private const string GoodQuality = "It is of good quality.";
    private const string FewWeeks = "Only a few weeks mining remain here.";
    private const string Stop = "You stop prospecting.";
    private const string Salt = "You will find salt here!";
    private const string Flint = "You will find flint here!";
    private const string MineForwards = "You start to mine forwards.";

    [Fact]
    public void Process_IgnoresFragmentsBeforeStart()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Iron));
        Assert.Null(assembler.Process(Quality));
        Assert.Null(assembler.Process(Remaining));
    }

    [Fact]
    public void Process_CreatesResultForCompleteConfirmedSequence()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Zinc));
        Assert.Null(assembler.Process(Quality));

        var result = assembler.Process(Remaining);

        var vein = Assert.IsType<ProspectingResult.Vein>(result);
        Assert.Equal(VeinMaterial.Zinc, vein.Material);
        Assert.Equal(12.5m, Assert.IsType<QualityMeasurement.ExactMax>(vein.Quality).Value);
        Assert.Equal(1103, Assert.IsType<RemainingMeasurement.Exact>(vein.Remaining).Value);
    }

    [Fact]
    public void Process_EmitsProgressiveObservationUpdatesWithoutFinalizingSequence()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Iron));

        Assert.True(assembler.TryTakeObservationUpdate(out var material));
        Assert.Equal(VeinMaterial.Iron, material.Material);
        Assert.IsType<QualityMeasurement.Unknown>(material.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(material.Remaining);
        Assert.False(material.IsSynchronized);
        Assert.False(assembler.TryTakeObservationUpdate(out _));

        Assert.Null(assembler.Process(Quality));
        Assert.True(assembler.TryTakeObservationUpdate(out var quality));
        Assert.IsType<QualityMeasurement.ExactMax>(quality.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(quality.Remaining);
        Assert.False(quality.IsSynchronized);

        Assert.IsType<ProspectingResult.Vein>(assembler.Process(Remaining));
        Assert.True(assembler.TryTakeObservationUpdate(out var remaining));
        Assert.IsType<RemainingMeasurement.Exact>(remaining.Remaining);
        Assert.True(remaining.IsSynchronized);
    }

    [Fact]
    public void Process_RecognizesGoldAsOreObservationAndCompleteVeinResult()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Gold));
        Assert.True(assembler.TryTakeObservationUpdate(out var material));
        Assert.Equal(VeinMaterial.Gold, material.Material);
        Assert.IsType<QualityMeasurement.Unknown>(material.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(material.Remaining);
        Assert.False(material.IsSynchronized);

        Assert.Null(assembler.Process(Quality));
        var result = Assert.IsType<ProspectingResult.Vein>(assembler.Process(Remaining));

        Assert.Equal(VeinMaterial.Gold, result.Material);
        Assert.Equal(12.5m, Assert.IsType<QualityMeasurement.ExactMax>(result.Quality).Value);
        Assert.Equal(1103, Assert.IsType<RemainingMeasurement.Exact>(result.Remaining).Value);
    }

    [Fact]
    public void Process_CreatesResultForCompleteEstimatedSequence()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Iron));
        Assert.Null(assembler.Process(GoodQuality));
        Assert.Null(assembler.Process(Salt));

        var vein = Assert.IsType<ProspectingResult.Vein>(assembler.Process(FewWeeks));

        Assert.Equal(VeinMaterial.Iron, vein.Material);
        Assert.Equal(ProspectingQualityGrade.Good, Assert.IsType<QualityMeasurement.Grade>(vein.Quality).Value);
        Assert.Equal(ProspectingRemainingBand.FewWeeks, Assert.IsType<RemainingMeasurement.EstimateBand>(vein.Remaining).Value);
        Assert.Empty(vein.Augments);
    }

    [Fact]
    public void Process_IgnoresNoiseBetweenConfirmedFragments()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process("Some unrelated message."));
        Assert.Null(assembler.Process(Iron));
        Assert.Null(assembler.Process("OtherPlayerA mines some ore."));
        Assert.Null(assembler.Process(Quality));
        Assert.Null(assembler.Process("Another unrelated message."));

        var result = assembler.Process(Remaining);

        var vein = Assert.IsType<ProspectingResult.Vein>(result);
        Assert.Equal(VeinMaterial.Iron, vein.Material);
    }

    [Fact]
    public void Process_NewStartDiscardsPreviousPartialSequence()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Iron));
        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Zinc));
        Assert.Null(assembler.Process(Quality));

        var result = assembler.Process(Remaining);

        var vein = Assert.IsType<ProspectingResult.Vein>(result);
        Assert.Equal(VeinMaterial.Zinc, vein.Material);
    }

    [Fact]
    public void Process_StopAfterMaterialDiscardsPartialSequence()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Iron));
        Assert.Null(assembler.Process(Stop));
        Assert.Null(assembler.Process(Quality));
        Assert.Null(assembler.Process(Remaining));
    }

    [Fact]
    public void Process_StopWithoutActivePartialIsNoOp()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Stop));
    }

    [Fact]
    public void Process_StopAfterQualityDoesNotEmitResult()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Iron));
        Assert.Null(assembler.Process(Quality));
        Assert.Null(assembler.Process(Stop));
        Assert.Null(assembler.Process(Remaining));
    }

    [Fact]
    public void Process_NewSequenceAfterStopCreatesVeinResult()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Iron));
        Assert.Null(assembler.Process(Stop));
        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Zinc));
        Assert.Null(assembler.Process(Quality));

        var result = Assert.IsType<ProspectingResult.Vein>(assembler.Process(Remaining));

        Assert.Equal(VeinMaterial.Zinc, result.Material);
    }

    [Fact]
    public void Process_DoesNotCreateResultForFragmentsOutsideConfirmedOrder()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Quality));
        Assert.Null(assembler.Process(Iron));
        Assert.Null(assembler.Process(Remaining));
    }

    [Fact]
    public void Process_ClearsStateAfterCompleteResult()
    {
        var assembler = new ProspectingSequenceAssembler();

        CompleteSequence(assembler, Iron);

        Assert.Null(assembler.Process(Iron));
        Assert.Null(assembler.Process(Quality));
        Assert.Null(assembler.Process(Remaining));
    }

    [Fact]
    public void Process_CreatesPlainRockResultWhenExactMaxQualityFollowsPlainRock()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.IsType<QualityMeasurement.Unknown>(
            Assert.IsType<ProspectingResult.PlainRock>(assembler.Process(PlainRock)).Quality);

        var result = Assert.IsType<ProspectingResult.PlainRock>(assembler.Process(Quality));

        Assert.Equal(12.5m, Assert.IsType<QualityMeasurement.ExactMax>(result.Quality).Value);
        Assert.Empty(result.Augments);
        Assert.Null(assembler.Process(Remaining));
    }

    [Theory]
    [InlineData(PlainRock, "It is of poor quality.", ProspectingQualityGrade.Poor)]
    [InlineData(PlainRock, "It is of acceptable quality.", ProspectingQualityGrade.Acceptable)]
    [InlineData(PlainRock, "It is of normal quality.", ProspectingQualityGrade.Normal)]
    [InlineData(PlainRock, "It is of good quality.", ProspectingQualityGrade.Good)]
    [InlineData(PlainRock, "It is of very good quality.", ProspectingQualityGrade.VeryGood)]
    [InlineData(PlainRock, "It is of utmost quality.", ProspectingQualityGrade.Utmost)]
    [InlineData(StoneRockPlainRock, "It is of normal quality.", ProspectingQualityGrade.Normal)]
    public void Process_CreatesPlainRockResultWhenGradeQualityFollowsRealPlainRock(
        string plainRock,
        string qualityMessage,
        ProspectingQualityGrade expectedGrade)
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.IsType<QualityMeasurement.Unknown>(
            Assert.IsType<ProspectingResult.PlainRock>(assembler.Process(plainRock)).Quality);

        var result = Assert.IsType<ProspectingResult.PlainRock>(assembler.Process(qualityMessage));

        Assert.Equal(expectedGrade, Assert.IsType<QualityMeasurement.Grade>(result.Quality).Value);
        Assert.Empty(result.Augments);
        Assert.Null(assembler.Process(Remaining));
    }

    [Theory]
    [InlineData(PlainRock)]
    [InlineData(StoneRockPlainRock)]
    [InlineData(FindOnlyRock)]
    public void Process_CreatesMaterialOnlyPlainRockWithoutQuality(string plainRock)
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        var result = Assert.IsType<ProspectingResult.PlainRock>(assembler.Process(plainRock));

        Assert.IsType<QualityMeasurement.Unknown>(result.Quality);
        Assert.Empty(result.Augments);
        Assert.Null(assembler.Process("Some unrelated message."));
    }

    [Fact]
    public void Process_DoesNotFinalizeVeinMaterialWithGradeWithoutRemaining()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Iron));
        Assert.Null(assembler.Process(GoodQuality));
    }

    [Fact]
    public void Process_NewStartDiscardsMaterialOnlyPlainRockBeforeQuality()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.IsType<ProspectingResult.PlainRock>(assembler.Process(PlainRock));
        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Zinc));
        Assert.Null(assembler.Process(Quality));

        var result = Assert.IsType<ProspectingResult.Vein>(assembler.Process(Remaining));

        Assert.Equal(VeinMaterial.Zinc, result.Material);
    }

    [Fact]
    public void Process_StopDiscardsMaterialOnlyPlainRockBeforeQuality()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.IsType<ProspectingResult.PlainRock>(assembler.Process(PlainRock));
        Assert.Null(assembler.Process(Stop));
        Assert.Null(assembler.Process(Quality));
    }

    [Fact]
    public void Process_FinalizesRockSaltWithUnknownRemainingWhenDirectSaltFollowsQuality()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(RockSalt));
        Assert.Null(assembler.Process(Quality));

        var result = Assert.IsType<ProspectingResult.Vein>(assembler.Process(Salt));

        Assert.Equal(VeinMaterial.RockSalt, result.Material);
        Assert.Equal(12.5m, Assert.IsType<QualityMeasurement.ExactMax>(result.Quality).Value);
        Assert.IsType<RemainingMeasurement.Unknown>(result.Remaining);
        Assert.Empty(result.Augments);
        Assert.Null(assembler.Process(Salt));
    }

    [Fact]
    public void Process_FinalizesRockSaltWhenGradeAndSaltFollowSaltRockSaltAlias()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(SaltRockSalt));
        Assert.Null(assembler.Process("It is of poor quality."));

        var result = Assert.IsType<ProspectingResult.Vein>(assembler.Process(Salt));

        Assert.Equal(VeinMaterial.RockSalt, result.Material);
        Assert.Equal(ProspectingQualityGrade.Poor, Assert.IsType<QualityMeasurement.Grade>(result.Quality).Value);
        Assert.IsType<RemainingMeasurement.Unknown>(result.Remaining);
        Assert.Null(assembler.Process(Salt));
    }

    [Theory]
    [InlineData("It is of poor quality.", ProspectingQualityGrade.Poor)]
    [InlineData("It is of acceptable quality.", ProspectingQualityGrade.Acceptable)]
    [InlineData("It is of normal quality.", ProspectingQualityGrade.Normal)]
    [InlineData("It is of good quality.", ProspectingQualityGrade.Good)]
    [InlineData("It is of very good quality.", ProspectingQualityGrade.VeryGood)]
    [InlineData("It is of utmost quality.", ProspectingQualityGrade.Utmost)]
    public void Process_FinalizesRockSaltWithUnknownRemainingWhenDirectSaltFollowsGradeQuality(
        string qualityMessage,
        ProspectingQualityGrade expectedGrade)
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(RockSalt));
        Assert.Null(assembler.Process(qualityMessage));

        var result = Assert.IsType<ProspectingResult.Vein>(assembler.Process(Salt));

        Assert.Equal(VeinMaterial.RockSalt, result.Material);
        Assert.Equal(expectedGrade, Assert.IsType<QualityMeasurement.Grade>(result.Quality).Value);
        Assert.IsType<RemainingMeasurement.Unknown>(result.Remaining);
        Assert.Empty(result.Augments);
        Assert.Null(assembler.Process(Salt));
    }

    [Fact]
    public void Process_DoesNotFinalizeRockSaltAfterQualityAlone()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(RockSalt));

        Assert.Null(assembler.Process(Quality));
    }

    [Fact]
    public void Process_DoesNotFinalizeRockSaltAfterGradeQualityAlone()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(RockSalt));

        Assert.Null(assembler.Process(GoodQuality));
    }

    [Theory]
    [InlineData(Flint)]
    [InlineData(MineForwards)]
    [InlineData(Start)]
    [InlineData(Stop)]
    public void Process_DoesNotFinalizeRockSaltWhenSaltIsNotDirectlyAfterQuality(string interveningLine)
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(RockSalt));
        Assert.Null(assembler.Process(Quality));
        Assert.Null(assembler.Process(interveningLine));

        Assert.Null(assembler.Process(Salt));
    }

    [Fact]
    public void Reset_ClearsPartialSequence()
    {
        var assembler = new ProspectingSequenceAssembler();

        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Iron));
        assembler.Reset();

        Assert.Null(assembler.Process(Quality));
        Assert.Null(assembler.Process(Remaining));
    }

    private static ProspectingResult.Vein CompleteSequence(ProspectingSequenceAssembler assembler, string material)
    {
        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(material));
        Assert.Null(assembler.Process(Quality));

        return Assert.IsType<ProspectingResult.Vein>(assembler.Process(Remaining));
    }
}
