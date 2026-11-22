using WurmMiningCounter.Models;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class ProfilePlainRockProspectingResultViewModelTests
{
    [Theory]
    [InlineData(ProspectingQualityGrade.Acceptable)]
    [InlineData(ProspectingQualityGrade.VeryGood)]
    public void FormatsGradeWithTheCanonicalQualityFormatter(ProspectingQualityGrade grade)
    {
        var viewModel = new ProfilePlainRockProspectingResultViewModel(
            new ProspectingResult.PlainRock(
                new QualityMeasurement.Grade(grade),
                []));

        Assert.Equal("Plain Rock", viewModel.Title);
        Assert.True(viewModel.HasKnownQuality);
        Assert.Equal(
            ProfileVeinContextViewModel.FormatQuality(new QualityMeasurement.Grade(grade)),
            viewModel.QualityText);
    }

    [Fact]
    public void FormatsExactMaxWithoutRemainingOrConfidence()
    {
        var viewModel = new ProfilePlainRockProspectingResultViewModel(
            new ProspectingResult.PlainRock(
                new QualityMeasurement.ExactMax(12.5m),
                []));

        Assert.Equal(
            ProfileVeinContextViewModel.FormatQuality(new QualityMeasurement.ExactMax(12.5m)),
            viewModel.QualityText);
        Assert.True(viewModel.HasKnownQuality);
    }

    [Fact]
    public void UnknownQualityShowsPlainRockWithoutQualityDetails()
    {
        var viewModel = new ProfilePlainRockProspectingResultViewModel(
            new ProspectingResult.PlainRock(new QualityMeasurement.Unknown(), []));

        Assert.Equal("Plain Rock", viewModel.Title);
        Assert.False(viewModel.HasKnownQuality);
    }
}
