namespace WurmMiningCounter.Models;

public sealed record ProspectingObservationUpdate(
    VeinMaterial Material,
    QualityMeasurement Quality,
    RemainingMeasurement Remaining,
    bool IsSynchronized);
