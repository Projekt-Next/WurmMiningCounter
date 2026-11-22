namespace WurmMiningCounter.Models;

public sealed record NearbyProspectingObservation(
    string RawDescription,
    IReadOnlyList<NearbyProspectingEntry> Entries,
    bool IsOnlyRock);

public sealed record NearbyProspectingEntry(
    string MaterialText,
    string? GradeText);
