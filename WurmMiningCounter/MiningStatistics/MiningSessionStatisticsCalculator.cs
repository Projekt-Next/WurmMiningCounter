namespace WurmMiningCounter.MiningStatistics;

public sealed record MiningSessionStatistics(
    decimal? TickRatePercent,
    decimal? ActionsPerTick,
    decimal? AverageGainPerTick,
    decimal? TicksPerHour);

public static class MiningSessionStatisticsCalculator
{
    public static MiningSessionStatistics Calculate(
        long successfulActions,
        long sessionMiningTicks,
        decimal sessionMiningGain,
        TimeSpan elapsed)
    {
        var tickRate = successfulActions == 0
            ? null
            : DivideAndMultiply(sessionMiningTicks, successfulActions, 100m);
        var actionsPerTick = sessionMiningTicks == 0
            ? null
            : Divide(successfulActions, sessionMiningTicks);
        var averageGainPerTick = sessionMiningTicks == 0
            ? null
            : Divide(sessionMiningGain, sessionMiningTicks);
        var ticksPerHour = elapsed <= TimeSpan.Zero
            ? null
            : MultiplyAndDivide(sessionMiningTicks, TimeSpan.TicksPerHour, elapsed.Ticks);

        return new MiningSessionStatistics(tickRate, actionsPerTick, averageGainPerTick, ticksPerHour);
    }

    public static decimal? CalculateTicksPerSelectedActions(
        long successfulActions,
        long sessionMiningTicks,
        long selectedActionCount)
    {
        if (successfulActions == 0 || selectedActionCount <= 0)
            return null;

        return DivideAndMultiply(sessionMiningTicks, successfulActions, selectedActionCount);
    }

    private static decimal? Divide(decimal dividend, decimal divisor)
    {
        try { return dividend / divisor; }
        catch (OverflowException) { return null; }
    }

    private static decimal? DivideAndMultiply(decimal dividend, decimal divisor, decimal multiplier)
    {
        try { return dividend / divisor * multiplier; }
        catch (OverflowException) { return null; }
    }

    private static decimal? MultiplyAndDivide(decimal multiplicand, decimal multiplier, decimal divisor)
    {
        try { return multiplicand * multiplier / divisor; }
        catch (OverflowException) { return null; }
    }
}
