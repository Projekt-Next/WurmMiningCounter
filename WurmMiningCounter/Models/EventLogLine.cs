namespace WurmMiningCounter.Models;

public sealed record EventLogLine(
    PlayerProfileId ProfileId,
    string ProfileName,
    string FilePath,
    string Text);
