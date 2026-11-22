namespace WurmMiningCounter.Models;

public abstract record EventPipelineOutput;

/// <summary>
/// Outcome of transferring one EventPipeline output to its immediate handoff layer.
/// It deliberately says nothing about later domain-worker completion.
/// </summary>
public enum EventPipelineHandoffResult
{
    Complete,
    NotStarted,
    Indeterminate
}

public sealed record EventReadBatch : EventPipelineOutput
{
    public EventReadBatch(
        PlayerProfileId profileId,
        string profileName,
        string canonicalPath,
        long sourceGeneration,
        IReadOnlyList<string> lines)
    {
        ProfileId = profileId;
        ProfileName = profileName;
        CanonicalPath = canonicalPath;
        SourceGeneration = sourceGeneration;
        Lines = Array.AsReadOnly(lines.ToArray());
    }

    public string ProfileName { get; }
    public PlayerProfileId ProfileId { get; }
    public string CanonicalPath { get; }
    public long SourceGeneration { get; }
    public IReadOnlyList<string> Lines { get; }
}

public sealed record EventContinuityLoss : EventPipelineOutput
{
    public EventContinuityLoss(
        PlayerProfileId profileId,
        string profileName,
        string canonicalPath,
        long sourceGeneration,
        StreamGapReason reason)
    {
        ProfileId = profileId;
        ProfileName = profileName;
        CanonicalPath = canonicalPath;
        SourceGeneration = sourceGeneration;
        Reason = reason;
    }

    public PlayerProfileId ProfileId { get; }
    public string ProfileName { get; }
    public string CanonicalPath { get; }
    public long SourceGeneration { get; }
    public StreamGapReason Reason { get; }
}
