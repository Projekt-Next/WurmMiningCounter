namespace WurmMiningCounter.Services;

public static class MiningInspirationParser
{
    private const string MomentOfInspiration = "You have a moment of inspiration...";

    public static bool IsMomentOfInspiration(string line) =>
        string.Equals(
            EventLogLineNormalizer.Normalize(line),
            MomentOfInspiration,
            StringComparison.Ordinal);
}
