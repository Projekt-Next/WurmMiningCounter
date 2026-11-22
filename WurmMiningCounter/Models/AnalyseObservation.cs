namespace WurmMiningCounter.Models;

public enum AnalyseInput { Shard, Ore }

public enum AnalyseStrength { Trace, Slight, Faint, Minuscule, Vague, Indistinct }

public sealed record AnalyseTrace(
    AnalyseStrength Strength,
    string? Material,
    string? Quality,
    string? Direction,
    string RawText);

public sealed record AnalyseNoTrace(AnalyseStrength? Strength, string RawText);
