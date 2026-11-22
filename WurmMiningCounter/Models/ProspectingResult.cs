using System.Collections.Immutable;

namespace WurmMiningCounter.Models;

public abstract record ProspectingResult
{
    public abstract ImmutableArray<ProspectingAugment> Augments { get; init; }

    public sealed record Vein(
        VeinMaterial Material,
        QualityMeasurement Quality,
        RemainingMeasurement Remaining,
        ImmutableArray<ProspectingAugment> Augments) : ProspectingResult;

    public sealed record PlainRock(
        QualityMeasurement Quality,
        ImmutableArray<ProspectingAugment> Augments) : ProspectingResult;
}
