namespace WurmMiningCounter.Models;

public sealed class EventLogFileCandidate
{
    public string ProfileName { get; init; } = string.Empty;

    public string ProfileDirectoryPath { get; init; } = string.Empty;

    public string EventFilePath { get; init; } = string.Empty;

    public DateTime CreationTimeUtc { get; init; }
}
