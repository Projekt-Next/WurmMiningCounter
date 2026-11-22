using WurmMiningCounter.Models;

namespace WurmMiningCounter.ViewModels;

public sealed class ProfileVeinContextViewModel
{
    public ProfileVeinContextViewModel(VeinContextSnapshot snapshot, int? localExactRemainingDelta = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ProfileId = snapshot.ProfileId;
        ProfileName = snapshot.ProfileName;
        Material = snapshot.Material;
        Quality = snapshot.Quality;
        Remaining = snapshot.Remaining;
        Confidence = snapshot.Confidence;
        LocalExactRemainingDelta = localExactRemainingDelta;
    }

    public PlayerProfileId ProfileId { get; }

    public string ProfileName { get; }

    public VeinMaterial Material { get; }

    public QualityMeasurement Quality { get; }

    public string QualityText => FormatQuality(Quality);

    public static string FormatQuality(QualityMeasurement quality) => quality switch
    {
        QualityMeasurement.Grade { Value: ProspectingQualityGrade.Poor } => "Poor quality (20–29)",
        QualityMeasurement.Grade { Value: ProspectingQualityGrade.Acceptable } => "Acceptable quality (30–39)",
        QualityMeasurement.Grade { Value: ProspectingQualityGrade.Normal } => "Normal quality (40–59)",
        QualityMeasurement.Grade { Value: ProspectingQualityGrade.Good } => "Good quality (60–79)",
        QualityMeasurement.Grade { Value: ProspectingQualityGrade.VeryGood } => "Very good quality (80–94)",
        QualityMeasurement.Grade { Value: ProspectingQualityGrade.Utmost } => "Utmost quality (95–99)",
        QualityMeasurement.ExactMax exactMax => $"Max {exactMax.Value}",
        _ => "Unknown"
    };

    public bool HasKnownQuality => Quality is not QualityMeasurement.Unknown;

    public RemainingMeasurement Remaining { get; }

    public string RemainingText => Remaining switch
    {
        RemainingMeasurement.EstimateBand { Value: ProspectingRemainingBand.Plenty } => "≈ 10,000+",
        RemainingMeasurement.EstimateBand { Value: ProspectingRemainingBand.FewWeeks } => "≈ 5,000–10,000",
        RemainingMeasurement.EstimateBand { Value: ProspectingRemainingBand.StartingToDeplete } => "≈ 3,000–5,000",
        RemainingMeasurement.EstimateBand { Value: ProspectingRemainingBand.ProspectAnotherVein } => "≈ 1,000–3,000",
        RemainingMeasurement.EstimateBand { Value: ProspectingRemainingBand.RunOutSoon } => "≈ 100–1,000",
        RemainingMeasurement.EstimateBand { Value: ProspectingRemainingBand.RunOutAnyHour } => "≈ 1–100",
        RemainingMeasurement.Exact exact when LocalExactRemainingDelta is { } delta => $"↓{delta} {exact.Value}",
        RemainingMeasurement.Exact exact => $"{exact.Value}",
        _ => "Unknown"
    };

    public bool HasKnownRemaining => Remaining is not RemainingMeasurement.Unknown;

    public string? LocalExactRemainingDeltaText => LocalExactRemainingDelta is { } delta
        ? $"↓{delta}"
        : null;

    public string RemainingValueText => Remaining switch
    {
        RemainingMeasurement.Exact exact => $"{exact.Value}",
        _ => RemainingText
    };

    public VeinConfidence Confidence { get; }

    public int? LocalExactRemainingDelta { get; }

    public string ConfidenceText => Confidence switch
    {
        VeinConfidence.TypeOnly => "Material only",
        VeinConfidence.Partial => "Partial",
        VeinConfidence.Synchronized => "Synchronized",
        VeinConfidence.Tracking => "Tracking",
        VeinConfidence.Ambiguous => "Ambiguous",
        _ => "Unknown"
    };
}
