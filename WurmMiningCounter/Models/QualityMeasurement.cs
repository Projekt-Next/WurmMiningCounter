namespace WurmMiningCounter.Models;

public abstract record QualityMeasurement
{
    public sealed record Unknown : QualityMeasurement;

    public sealed record Grade(ProspectingQualityGrade Value) : QualityMeasurement;

    public sealed record ExactMax(decimal Value) : QualityMeasurement;
}
