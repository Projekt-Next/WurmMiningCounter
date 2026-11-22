namespace WurmMiningCounter.Models;

public abstract record RemainingMeasurement
{
    public sealed record Unknown : RemainingMeasurement;

    public sealed record EstimateBand(ProspectingRemainingBand Value) : RemainingMeasurement;

    public sealed record Exact(int Value) : RemainingMeasurement;
}
