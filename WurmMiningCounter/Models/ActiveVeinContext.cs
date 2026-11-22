using System.Collections.Immutable;

namespace WurmMiningCounter.Models;

public sealed record ActiveVeinContext(
    VeinMaterial Material,
    QualityMeasurement Quality,
    RemainingMeasurement Remaining,
    VeinConfidence Confidence)
{
    public ImmutableArray<ProspectingAugment> Augments { get; init; } =
        ImmutableArray<ProspectingAugment>.Empty;
}
