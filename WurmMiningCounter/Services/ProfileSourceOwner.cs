using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

internal enum ProfileSourceOwnershipStatus { Verified, Unavailable, Changed }

/// <summary>
/// One source's ownership registration. Confirmed evidence is checked at source
/// open boundaries; an unconfirmed registration retains a directory read guard.
/// Neither case treats the path alone as proof that an owner stayed unchanged.
/// </summary>
internal sealed class ProfileSourceOwner(
    PlayerProfileIdentityResolver resolver,
    ResolvedPlayerProfileIdentity identity,
    string profileDirectoryPath,
    string? physicalIdentity,
    Action? release,
    bool requiresEofBaseline = false,
    Guid sourceId = default) : IDisposable
{
    private int _disposed;
    internal ResolvedPlayerProfileIdentity Identity { get; } = identity;
    internal string ProfileDirectoryPath { get; } = profileDirectoryPath;
    internal string? PhysicalIdentity { get; } = physicalIdentity;
    internal bool RequiresEofBaseline { get; } = requiresEofBaseline;
    internal Guid SourceId { get; } = sourceId;

    internal ProfileSourceOwnershipStatus TryOpenRead(out IDisposable? readGuard)
    {
        var ownership = Volatile.Read(ref _disposed) == 0
            ? resolver.TryOpenSourceRead(this, out readGuard)
            : Unavailable(out readGuard);
        if (ownership != ProfileSourceOwnershipStatus.Verified)
            resolver.MarkSourceSuspended(SourceId);
        return ownership;
    }

    internal void MarkActivated() => resolver.MarkSourceActive(SourceId);
    internal void MarkSuspended() => resolver.MarkSourceSuspended(SourceId);
    internal void MarkOwnershipConflict() => resolver.MarkSourceOwnershipConflict(SourceId);
    internal void MarkRestartRequired() => resolver.MarkSourceRestartRequired(SourceId);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            release?.Invoke();
            resolver.ReleaseSource(SourceId);
        }
    }

    private static ProfileSourceOwnershipStatus Unavailable(out IDisposable? readGuard)
    {
        readGuard = null;
        return ProfileSourceOwnershipStatus.Unavailable;
    }
}
