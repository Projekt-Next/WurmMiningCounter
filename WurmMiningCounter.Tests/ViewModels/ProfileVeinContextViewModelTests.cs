using WurmMiningCounter.Models;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class ProfileVeinContextViewModelTests
{
    [Theory]
    [MemberData(nameof(QualityCases))]
    public void QualityText_FormatsAllMeasurementVariants(
        QualityMeasurement quality,
        string expectedText)
    {
        var viewModel = CreateViewModel(quality: quality);

        Assert.Equal(expectedText, viewModel.QualityText);
    }

    [Theory]
    [MemberData(nameof(RemainingCases))]
    public void RemainingText_FormatsAllMeasurementVariants(
        RemainingMeasurement remaining,
        string expectedText)
    {
        var viewModel = CreateViewModel(remaining: remaining);

        Assert.Equal(expectedText, viewModel.RemainingText);
    }

    [Fact]
    public void RemainingText_FormatsLocalExactDeltaBeforeExactRemaining()
    {
        var viewModel = CreateViewModel(
            remaining: new RemainingMeasurement.Exact(6707),
            localExactRemainingDelta: 3);

        Assert.Equal("↓3 6707", viewModel.RemainingText);
        Assert.Equal("↓3", viewModel.LocalExactRemainingDeltaText);
        Assert.Equal("6707", viewModel.RemainingValueText);
    }

    [Fact]
    public void UnknownMeasurements_AreNotMarkedForRendering()
    {
        var viewModel = CreateViewModel(
            quality: new QualityMeasurement.Unknown(),
            remaining: new RemainingMeasurement.Unknown());

        Assert.False(viewModel.HasKnownQuality);
        Assert.False(viewModel.HasKnownRemaining);
    }

    [Fact]
    public void KnownMeasurements_AreMarkedForRendering()
    {
        var viewModel = CreateViewModel(
            quality: new QualityMeasurement.Grade(ProspectingQualityGrade.Good),
            remaining: new RemainingMeasurement.EstimateBand(ProspectingRemainingBand.FewWeeks));

        Assert.True(viewModel.HasKnownQuality);
        Assert.True(viewModel.HasKnownRemaining);
        Assert.Null(viewModel.LocalExactRemainingDeltaText);
        Assert.Equal("≈ 5,000–10,000", viewModel.RemainingValueText);
    }

    [Theory]
    [InlineData(ProspectingRemainingBand.FewWeeks, "≈ 5,000–10,000")]
    [InlineData(null, "Unknown")]
    public void RemainingText_DoesNotDisplayLocalExactDeltaForEstimatedOrUnknownRemaining(
        ProspectingRemainingBand? band,
        string expectedText)
    {
        RemainingMeasurement remaining = band is { } value
            ? new RemainingMeasurement.EstimateBand(value)
            : new RemainingMeasurement.Unknown();

        var viewModel = CreateViewModel(remaining: remaining, localExactRemainingDelta: 3);

        Assert.Equal(expectedText, viewModel.RemainingText);
    }

    [Theory]
    [InlineData(VeinConfidence.TypeOnly, "Material only")]
    [InlineData(VeinConfidence.Partial, "Partial")]
    [InlineData(VeinConfidence.Synchronized, "Synchronized")]
    [InlineData(VeinConfidence.Tracking, "Tracking")]
    [InlineData(VeinConfidence.Ambiguous, "Ambiguous")]
    [InlineData(VeinConfidence.Unknown, "Unknown")]
    public void ConfidenceText_FormatsAllVariants(
        VeinConfidence confidence,
        string expectedText)
    {
        var viewModel = CreateViewModel(confidence: confidence);

        Assert.Equal(expectedText, viewModel.ConfidenceText);
    }

    public static IEnumerable<object[]> QualityCases =>
    [
        [new QualityMeasurement.Grade(ProspectingQualityGrade.Poor), "Poor quality (20–29)"],
        [new QualityMeasurement.Grade(ProspectingQualityGrade.Acceptable), "Acceptable quality (30–39)"],
        [new QualityMeasurement.Grade(ProspectingQualityGrade.Normal), "Normal quality (40–59)"],
        [new QualityMeasurement.Grade(ProspectingQualityGrade.Good), "Good quality (60–79)"],
        [new QualityMeasurement.Grade(ProspectingQualityGrade.VeryGood), "Very good quality (80–94)"],
        [new QualityMeasurement.Grade(ProspectingQualityGrade.Utmost), "Utmost quality (95–99)"],
        [new QualityMeasurement.ExactMax(12m), "Max 12"],
        [new QualityMeasurement.Unknown(), "Unknown"]
    ];

    public static IEnumerable<object[]> RemainingCases =>
    [
        [new RemainingMeasurement.EstimateBand(ProspectingRemainingBand.Plenty), "≈ 10,000+"],
        [new RemainingMeasurement.EstimateBand(ProspectingRemainingBand.FewWeeks), "≈ 5,000–10,000"],
        [new RemainingMeasurement.EstimateBand(ProspectingRemainingBand.StartingToDeplete), "≈ 3,000–5,000"],
        [new RemainingMeasurement.EstimateBand(ProspectingRemainingBand.ProspectAnotherVein), "≈ 1,000–3,000"],
        [new RemainingMeasurement.EstimateBand(ProspectingRemainingBand.RunOutSoon), "≈ 100–1,000"],
        [new RemainingMeasurement.EstimateBand(ProspectingRemainingBand.RunOutAnyHour), "≈ 1–100"],
        [new RemainingMeasurement.Exact(1103), "1103"],
        [new RemainingMeasurement.Unknown(), "Unknown"]
    ];

    private static ProfileVeinContextViewModel CreateViewModel(
        QualityMeasurement? quality = null,
        RemainingMeasurement? remaining = null,
        VeinConfidence confidence = VeinConfidence.Synchronized,
        int? localExactRemainingDelta = null)
    {
        return new ProfileVeinContextViewModel(
            new VeinContextSnapshot(
                EventTransportTestProfileIds.Alice,
                "Alice",
                VeinMaterial.Iron,
                quality ?? new QualityMeasurement.ExactMax(12m),
                remaining ?? new RemainingMeasurement.Exact(1103),
                confidence),
            localExactRemainingDelta);
    }
}
