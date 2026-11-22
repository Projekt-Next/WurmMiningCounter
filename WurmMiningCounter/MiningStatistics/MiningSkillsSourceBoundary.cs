namespace WurmMiningCounter.MiningStatistics;

/// <summary>
/// Immutable identity of the complete-record prefix captured by one source reader at startup.
/// </summary>
public sealed class MiningSkillsSourceBoundary
{
    internal MiningSkillsSourceBoundary(
        string canonicalSourcePath,
        long position,
        MiningSkillsSourceReader.SourceFingerprint fingerprint)
    {
        CanonicalSourcePath = canonicalSourcePath;
        Position = position;
        Fingerprint = fingerprint;
    }

    public string CanonicalSourcePath { get; }

    /// <summary>Byte position immediately after the last complete LF-terminated record.</summary>
    public long Position { get; }

    internal MiningSkillsSourceReader.SourceFingerprint Fingerprint { get; }
}
