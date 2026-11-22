using System.Collections.Immutable;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Tests.Models;

public sealed class ProspectingResultTests
{
    [Fact]
    public void VeinStoresMeasurementsAndEmptyAugments()
    {
        var quality = new QualityMeasurement.ExactMax(12.5m);
        var remaining = new RemainingMeasurement.Exact(1103);

        var result = new ProspectingResult.Vein(
            VeinMaterial.Zinc,
            quality,
            remaining,
            ImmutableArray<ProspectingAugment>.Empty);

        Assert.Equal(VeinMaterial.Zinc, result.Material);
        Assert.Same(quality, result.Quality);
        Assert.Same(remaining, result.Remaining);
        Assert.Empty(result.Augments);
    }

    [Fact]
    public void PlainRockStoresQualityAndEmptyAugments()
    {
        var quality = new QualityMeasurement.ExactMax(12.5m);

        var result = new ProspectingResult.PlainRock(
            quality,
            ImmutableArray<ProspectingAugment>.Empty);

        Assert.Same(quality, result.Quality);
        Assert.Empty(result.Augments);
    }

    [Fact]
    public void VeinStoresSaltAndFlintAugments()
    {
        var result = new ProspectingResult.Vein(
            VeinMaterial.RockSalt,
            new QualityMeasurement.ExactMax(12.5m),
            new RemainingMeasurement.Unknown(),
            [ProspectingAugment.Salt, ProspectingAugment.Flint]);

        Assert.Equal([ProspectingAugment.Salt, ProspectingAugment.Flint], result.Augments);
    }

    [Fact]
    public void VeinStoresQualitativeQualityMeasurement()
    {
        var quality = new QualityMeasurement.Grade(ProspectingQualityGrade.Good);
        var result = new ProspectingResult.Vein(
            VeinMaterial.Iron,
            quality,
            new RemainingMeasurement.Unknown(),
            ImmutableArray<ProspectingAugment>.Empty);

        Assert.Same(quality, result.Quality);
        Assert.Equal(ProspectingQualityGrade.Good, Assert.IsType<QualityMeasurement.Grade>(result.Quality).Value);
    }

    [Fact]
    public void VeinStoresEstimatedRemainingMeasurement()
    {
        var remaining = new RemainingMeasurement.EstimateBand(ProspectingRemainingBand.FewWeeks);
        var result = new ProspectingResult.Vein(
            VeinMaterial.Iron,
            new QualityMeasurement.Unknown(),
            remaining,
            ImmutableArray<ProspectingAugment>.Empty);

        Assert.Same(remaining, result.Remaining);
        Assert.Equal(ProspectingRemainingBand.FewWeeks, Assert.IsType<RemainingMeasurement.EstimateBand>(result.Remaining).Value);
    }
}
