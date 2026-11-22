using System.Collections.Immutable;

namespace WurmMiningCounter.Models;

public sealed record AnalyseEntry(AnalyseTrace? Trace, AnalyseNoTrace? NoTrace, AnalyseStrength? StageMarker, string RawText);

public sealed record AnalyseResult(AnalyseInput Input, ImmutableArray<AnalyseEntry> Entries)
{
    public string? FinishTime { get; init; }
}

public sealed record AnalyseSnapshot(PlayerProfileId ProfileId, string ProfileName, AnalyseResult Result);

public sealed class AnalyseChangedEventArgs(PlayerProfileId profileId, AnalyseSnapshot? snapshot) : EventArgs
{
    public PlayerProfileId ProfileId { get; } = profileId;
    public AnalyseSnapshot? Snapshot { get; } = snapshot;
}
