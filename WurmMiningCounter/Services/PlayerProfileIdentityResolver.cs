using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

internal enum PlayerProfileIdentityActivationFailureReason
{
    ResolutionFailed,
    RegistryInvalid,
    RegistryUnavailable,
    OwnershipUnavailable,
    FallbackReactivationBlocked
}

internal sealed record PlayerProfileIdentityActivationStatus(
    string ProfileDirectoryPath,
    PlayerProfileIdentityActivationFailureReason? FailureReason,
    bool IsActive,
    bool IsPending,
    bool IsSuspended,
    bool IsOwnershipConflict = false,
    bool IsRestartRequired = false)
{
    internal bool HasIssue => FailureReason is not null || IsPending || IsSuspended;
}

/// <summary>
/// Resolves only verified profile directories. A physical directory identity
/// is the sole evidence allowed to carry an ID across a path change. Without
/// it, an ID is scoped to one normalized path and is explicitly unconfirmed.
/// </summary>
internal sealed class PlayerProfileIdentityResolver
{
    private const string RegistryFileName = "player-profile-identities.json";
    private static readonly ConcurrentDictionary<string, object> RegistryGates =
        new(WurmDiscoveryPathPolicy.Comparer);
    private readonly object _sync;
    private readonly IDirectoryPhysicalIdentityProvider _physicalIdentityProvider;
    private readonly PlayerProfileIdentityRegistry _registry;
    private readonly Dictionary<string, UnconfirmedSourceOwner> _unconfirmedSourceOwners = new(WurmDiscoveryPathPolicy.Comparer);
    private readonly Dictionary<string, string> _confirmedSourceEvidence = new(WurmDiscoveryPathPolicy.Comparer);
    private readonly Dictionary<string, PlayerProfileId> _confirmedSourceIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _confirmedSourceNamespaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlayerProfileIdentityActivationStatus> _activationStatuses = new(WurmDiscoveryPathPolicy.Comparer);
    private readonly Dictionary<Guid, SourceLifecycle> _sourceLifecycles = [];
    private readonly Dictionary<string, PlayerProfileIdentityActivationFailureReason> _activationFailures = new(WurmDiscoveryPathPolicy.Comparer);

    public PlayerProfileIdentityResolver()
        : this(SettingsService.GetDefaultSettingsDirectoryPath())
    {
    }

    public PlayerProfileIdentityResolver(string registryDirectoryPath)
        : this(registryDirectoryPath, new WindowsDirectoryPhysicalIdentityProvider())
    {
    }

    internal PlayerProfileIdentityResolver(
        string registryDirectoryPath,
        IDirectoryPhysicalIdentityProvider physicalIdentityProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryDirectoryPath);
        _physicalIdentityProvider = physicalIdentityProvider ?? throw new ArgumentNullException(nameof(physicalIdentityProvider));
        var registryFilePath = Path.Combine(Path.GetFullPath(registryDirectoryPath), RegistryFileName);
        _sync = RegistryGates.GetOrAdd(registryFilePath, static _ => new object());
        _registry = new PlayerProfileIdentityRegistry(registryFilePath);
    }

    public bool TryResolveVerifiedProfile(
        string profileDirectoryPath,
        out ResolvedPlayerProfileIdentity? resolution)
    {
        resolution = null;
        string canonicalPath;
        try
        {
            canonicalPath = WurmDiscoveryPathPolicy.NormalizeDirectoryPath(profileDirectoryPath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        string? physicalIdentity;
        try
        {
            physicalIdentity = NormalizePhysicalIdentity(_physicalIdentityProvider.GetIdentity(canonicalPath));
        }
        catch (UnauthorizedAccessException)
        {
            physicalIdentity = null;
        }
        catch (IOException)
        {
            physicalIdentity = null;
        }

        lock (_sync)
            return _registry.TryResolve(canonicalPath, physicalIdentity, out resolution);
    }

    internal bool TryResolveConfirmedProfile(string profileDirectoryPath, out ResolvedPlayerProfileIdentity? resolution)
    {
        resolution = null;
        string canonicalPath;
        try { canonicalPath = WurmDiscoveryPathPolicy.NormalizeDirectoryPath(profileDirectoryPath); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        var physicalIdentity = ReadPhysicalIdentity(canonicalPath);
        if (physicalIdentity is null)
            return false;
        lock (_sync)
            return _registry.TryResolve(canonicalPath, physicalIdentity, out resolution)
                && resolution?.Confidence == PlayerProfileIdentityConfidence.ConfirmedPhysicalIdentity;
    }

    internal event EventHandler<PlayerProfileIdentityActivationStatus>? ActivationStatusChanged;

    internal IReadOnlyList<PlayerProfileIdentityActivationStatus> GetActivationStatusSnapshot()
    {
        lock (_sync)
            return _activationStatuses.Values.ToArray();
    }

    private static string? NormalizePhysicalIdentity(string? physicalIdentity) =>
        string.IsNullOrWhiteSpace(physicalIdentity) ? null : physicalIdentity.Trim();

    internal bool TryRegisterSource(
        string profileDirectoryPath,
        out ProfileSourceOwner? owner,
        out IDisposable? readGuard)
    {
        owner = null;
        readGuard = null;
        string path;
        try { path = WurmDiscoveryPathPolicy.NormalizeDirectoryPath(profileDirectoryPath); }
        catch (ArgumentException) { return ReportBlockedOutsideLock(profileDirectoryPath, PlayerProfileIdentityActivationFailureReason.ResolutionFailed); }
        catch (NotSupportedException) { return ReportBlockedOutsideLock(profileDirectoryPath, PlayerProfileIdentityActivationFailureReason.ResolutionFailed); }

        PlayerProfileIdentityActivationStatus? previous;
        PlayerProfileIdentityActivationStatus? current;
        bool registered;
        lock (_sync)
        {
            previous = _activationStatuses.GetValueOrDefault(path);
            registered = TryRegisterSourceCore(path, out owner, out readGuard);
            current = _activationStatuses.GetValueOrDefault(path);
        }

        if (current is not null && current != previous)
            NotifyActivationStatusChanged(current);
        return registered;
    }

    private bool TryRegisterSourceCore(
        string path,
        out ProfileSourceOwner? owner,
        out IDisposable? readGuard)
    {
        owner = null;
        readGuard = null;
        if (_unconfirmedSourceOwners.TryGetValue(path, out var existing))
        {
            // The retained handle prevents this path from being rebound. Keep
            // the path-scoped ID; do not promote or merge it with confirmed IDs.
            if (!existing.ReadGuard.IsAvailable
                || !_registry.TryResolve(path, null, out var identity, requireSourceOwnership: true,
                requiredExistingId: existing.Identity.Id)
                || identity is null || identity.Id != existing.Identity.Id)
                return ReportBlocked(path, GetRegistryFailureReasonOr(PlayerProfileIdentityActivationFailureReason.FallbackReactivationBlocked));
            readGuard = RetainUnconfirmedOwner(path, existing);
            var sourceId = RegisterSource(path);
            owner = CreateUnconfirmedOwner(path, existing, sourceId);
            return true;
        }

        var guard = WindowsDirectoryPhysicalIdentityProvider.TryOpenReadGuard(path);
        if (guard is null)
            return ReportBlocked(path, PlayerProfileIdentityActivationFailureReason.OwnershipUnavailable);
        var transferred = false;
        try
        {
            var physicalIdentity = ReadPhysicalIdentity(path);
            var namespacePath = guard.CanonicalNamespacePath;
            // A differently spelled alias must not turn a live fallback
            // into a second confirmed (or unconfirmed) active identity.
            // Namespace equality is grounds to reject, not to merge IDs.
            if (_unconfirmedSourceOwners.Values.Any(existing => namespacePath is null
                || existing.ReadGuard.CanonicalNamespacePath is null
                || string.Equals(existing.ReadGuard.CanonicalNamespacePath, namespacePath, StringComparison.OrdinalIgnoreCase)))
                return ReportBlocked(path, PlayerProfileIdentityActivationFailureReason.FallbackReactivationBlocked);
            if (physicalIdentity is null && (namespacePath is null
                ? _confirmedSourceIds.Count != 0
                : _confirmedSourceNamespaces.Contains(namespacePath)))
                return ReportBlocked(path, PlayerProfileIdentityActivationFailureReason.FallbackReactivationBlocked);
            if (physicalIdentity is null && _confirmedSourceEvidence.ContainsKey(path))
                return ReportBlocked(path, PlayerProfileIdentityActivationFailureReason.FallbackReactivationBlocked);
            PlayerProfileId? requiredId = physicalIdentity is not null
                && _confirmedSourceIds.TryGetValue(physicalIdentity, out var knownId) ? knownId : null;
            if (!_registry.TryResolve(path, physicalIdentity, out var identity, requireSourceOwnership: true,
                requiredExistingId: requiredId)
                || identity is null)
                return ReportBlocked(path, GetRegistryFailureReasonOr(physicalIdentity is null
                    ? PlayerProfileIdentityActivationFailureReason.FallbackReactivationBlocked
                    : PlayerProfileIdentityActivationFailureReason.ResolutionFailed));
            if (physicalIdentity is null)
            {
                var retained = new UnconfirmedSourceOwner(identity, guard);
                _unconfirmedSourceOwners.Add(path, retained);
                owner = CreateUnconfirmedOwner(path, retained, RegisterSource(path));
                readGuard = RetainUnconfirmedOwner(path, retained);
            }
            else
            {
                var changedOwner = _confirmedSourceEvidence.TryGetValue(path, out var previous)
                    && !string.Equals(previous, physicalIdentity, StringComparison.Ordinal);
                _confirmedSourceEvidence[path] = physicalIdentity;
                _confirmedSourceIds[physicalIdentity] = identity.Id;
                if (namespacePath is not null)
                    _confirmedSourceNamespaces.Add(namespacePath);
                owner = new ProfileSourceOwner(this, identity, path, physicalIdentity, null, changedOwner, RegisterSource(path));
                readGuard = guard;
            }
            transferred = true;
            return true;
        }
        finally
        {
            if (!transferred)
                guard.Dispose();
        }
    }

    internal ProfileSourceOwnershipStatus TryOpenSourceRead(ProfileSourceOwner owner, out IDisposable? readGuard)
    {
        readGuard = null;
        lock (_sync)
        {
            if (owner.PhysicalIdentity is null)
            {
                if (!_unconfirmedSourceOwners.TryGetValue(owner.ProfileDirectoryPath, out var retained)
                    || retained.Identity.Id != owner.Identity.Id || !retained.ReadGuard.IsAvailable)
                    return ProfileSourceOwnershipStatus.Unavailable;
                readGuard = RetainUnconfirmedOwner(owner.ProfileDirectoryPath, retained);
                return ProfileSourceOwnershipStatus.Verified;
            }

            var guard = WindowsDirectoryPhysicalIdentityProvider.TryOpenReadGuard(owner.ProfileDirectoryPath);
            if (guard is null)
                return ProfileSourceOwnershipStatus.Unavailable;
            var transferred = false;
            try
            {
                var physicalIdentity = ReadPhysicalIdentity(owner.ProfileDirectoryPath);
                if (physicalIdentity is null)
                    return ProfileSourceOwnershipStatus.Unavailable;
                if (!string.Equals(physicalIdentity, owner.PhysicalIdentity, StringComparison.Ordinal))
                    return ProfileSourceOwnershipStatus.Changed;
                readGuard = guard;
                transferred = true;
                return ProfileSourceOwnershipStatus.Verified;
            }
            finally
            {
                if (!transferred)
                    guard.Dispose();
            }
        }
    }

    internal void MarkSourceActive(Guid sourceId) => UpdateSourceLifecycle(sourceId, SourceLifecycleState.Active);

    internal void MarkSourceSuspended(Guid sourceId) => UpdateSourceLifecycle(sourceId, SourceLifecycleState.Suspended);

    internal void MarkSourceOwnershipConflict(Guid sourceId) => UpdateSourceLifecycle(sourceId, SourceLifecycleState.OwnershipConflict);

    internal void MarkSourceRestartRequired(Guid sourceId) => UpdateSourceLifecycle(sourceId, SourceLifecycleState.RestartRequired);

    internal void ReleaseSource(Guid sourceId)
    {
        PlayerProfileIdentityActivationStatus? changed = null;
        lock (_sync)
        {
            if (_sourceLifecycles.Remove(sourceId, out var source))
                changed = RefreshActivationStatus(source.Path);
        }
        if (changed is not null)
            NotifyActivationStatusChanged(changed);
    }

    private string? ReadPhysicalIdentity(string path)
    {
        try { return NormalizePhysicalIdentity(_physicalIdentityProvider.GetIdentity(path)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private bool ReportBlocked(string profileDirectoryPath, PlayerProfileIdentityActivationFailureReason reason)
    {
        _activationFailures[profileDirectoryPath] = reason;
        RefreshActivationStatus(profileDirectoryPath);
        return false;
    }

    private bool ReportBlockedOutsideLock(string profileDirectoryPath, PlayerProfileIdentityActivationFailureReason reason)
    {
        PlayerProfileIdentityActivationStatus? changed;
        lock (_sync)
        {
            _activationFailures[profileDirectoryPath] = reason;
            changed = RefreshActivationStatus(profileDirectoryPath);
        }
        if (changed is not null)
            NotifyActivationStatusChanged(changed);
        return false;
    }

    private Guid RegisterSource(string profileDirectoryPath)
    {
        var sourceId = Guid.NewGuid();
        _activationFailures.Remove(profileDirectoryPath);
        _sourceLifecycles.Add(sourceId, new SourceLifecycle(profileDirectoryPath, SourceLifecycleState.Pending));
        RefreshActivationStatus(profileDirectoryPath);
        return sourceId;
    }

    private void UpdateSourceLifecycle(Guid sourceId, SourceLifecycleState state)
    {
        PlayerProfileIdentityActivationStatus? changed = null;
        lock (_sync)
        {
            if (_sourceLifecycles.TryGetValue(sourceId, out var source) && source.State != state)
            {
                _sourceLifecycles[sourceId] = source with { State = state };
                changed = RefreshActivationStatus(source.Path);
            }
        }
        if (changed is not null)
            NotifyActivationStatusChanged(changed);
    }

    private PlayerProfileIdentityActivationStatus? RefreshActivationStatus(string path)
    {
        var sources = _sourceLifecycles.Values.Where(source => WurmDiscoveryPathPolicy.Comparer.Equals(source.Path, path)).ToArray();
        var hasFailure = _activationFailures.TryGetValue(path, out var failure);
        if (sources.Length == 0 && !hasFailure)
        {
            if (!_activationStatuses.Remove(path))
                return null;
            return new PlayerProfileIdentityActivationStatus(path, null, false, false, false);
        }

        var status = new PlayerProfileIdentityActivationStatus(
            path,
            hasFailure ? failure : null,
            sources.Any(source => source.State == SourceLifecycleState.Active),
            sources.Any(source => source.State == SourceLifecycleState.Pending),
            sources.Any(source => source.State is SourceLifecycleState.Suspended or SourceLifecycleState.OwnershipConflict or SourceLifecycleState.RestartRequired),
            sources.Any(source => source.State == SourceLifecycleState.OwnershipConflict),
            sources.Any(source => source.State == SourceLifecycleState.RestartRequired));
        if (_activationStatuses.TryGetValue(path, out var existing) && existing == status)
            return null;

        _activationStatuses[path] = status;
        return status;
    }

    private void NotifyActivationStatusChanged(PlayerProfileIdentityActivationStatus status)
    {
        try { ActivationStatusChanged?.Invoke(this, status); }
        catch { }
    }

    private PlayerProfileIdentityActivationFailureReason GetRegistryFailureReasonOr(
        PlayerProfileIdentityActivationFailureReason fallback) => _registry.FailureReason switch
    {
        PlayerProfileIdentityRegistryFailureReason.Invalid => PlayerProfileIdentityActivationFailureReason.RegistryInvalid,
        PlayerProfileIdentityRegistryFailureReason.Unavailable => PlayerProfileIdentityActivationFailureReason.RegistryUnavailable,
        _ => fallback
    };

    private ProfileSourceOwner CreateUnconfirmedOwner(string path, UnconfirmedSourceOwner retained, Guid sourceId)
    {
        var reference = RetainUnconfirmedOwner(path, retained);
        return new ProfileSourceOwner(this, retained.Identity, path, null, reference.Dispose, false, sourceId);
    }

    private IDisposable RetainUnconfirmedOwner(string path, UnconfirmedSourceOwner retained)
    {
        retained.References++;
        return new SourceReference(() =>
        {
            lock (_sync)
            {
                if (--retained.References == 0)
                {
                    _unconfirmedSourceOwners.Remove(path);
                    retained.ReadGuard.Dispose();
                }
            }
        });
    }

    private sealed class UnconfirmedSourceOwner(ResolvedPlayerProfileIdentity identity,
        WindowsDirectoryPhysicalIdentityProvider.DirectoryReadGuard readGuard)
    {
        internal ResolvedPlayerProfileIdentity Identity { get; } = identity;
        internal WindowsDirectoryPhysicalIdentityProvider.DirectoryReadGuard ReadGuard { get; } = readGuard;
        internal int References { get; set; }
    }

    private sealed class SourceReference(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private enum SourceLifecycleState { Pending, Active, Suspended, OwnershipConflict, RestartRequired }

    private sealed record SourceLifecycle(string Path, SourceLifecycleState State);
}

internal enum PlayerProfileIdentityRegistryFailureReason { None, Invalid, Unavailable }

internal sealed class PlayerProfileIdentityRegistry
{
    private const string RegistryFileName = "player-profile-identities.json";
    private readonly string _registryFilePath;
    private readonly object _sync = new();
    private List<Entry> _entries = [];
    private bool _writesBlocked;
    internal PlayerProfileIdentityRegistryFailureReason FailureReason { get; private set; }

    public PlayerProfileIdentityRegistry(string registryFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryFilePath);
        _registryFilePath = Path.GetFullPath(registryFilePath);
    }

    public bool TryResolve(
        string canonicalProfileDirectoryPath,
        string? physicalIdentity,
        out ResolvedPlayerProfileIdentity? resolution,
        bool requireSourceOwnership = false,
        PlayerProfileId? requiredExistingId = null)
    {
        lock (_sync)
        {
            resolution = null;
            if (!ReloadCurrentEntries())
                return false;

            var confirmed = physicalIdentity is not null;
            var match = confirmed
                ? _entries.SingleOrDefault(entry => string.Equals(entry.PhysicalIdentity, physicalIdentity, StringComparison.Ordinal))
                : _entries.SingleOrDefault(entry => entry.PhysicalIdentity is null
                    && WurmDiscoveryPathPolicy.Comparer.Equals(entry.CanonicalProfileDirectoryPath, canonicalProfileDirectoryPath));

            if (requiredExistingId is { } expectedId
                && (match is null || !PlayerProfileId.TryParse(match.Id, out var matchedId) || matchedId != expectedId))
                return false;

            if (requireSourceOwnership && (confirmed
                ? match is null && _entries.Any(entry => entry.PhysicalIdentity is null
                    && WurmDiscoveryPathPolicy.Comparer.Equals(entry.CanonicalProfileDirectoryPath, canonicalProfileDirectoryPath))
                : _entries.Any(entry => entry.PhysicalIdentity is not null
                    && WurmDiscoveryPathPolicy.Comparer.Equals(entry.CanonicalProfileDirectoryPath, canonicalProfileDirectoryPath))))
                return false;

            // A persisted fallback has no physical proof across a lifetime gap.
            // Only a still-retained source handle may reuse that assignment.
            if (requireSourceOwnership && !confirmed && match is not null && requiredExistingId is null)
                return false;

            if (match is not null)
            {
                if (confirmed && !WurmDiscoveryPathPolicy.Comparer.Equals(match.CanonicalProfileDirectoryPath, canonicalProfileDirectoryPath))
                {
                    var updated = match with { CanonicalProfileDirectoryPath = canonicalProfileDirectoryPath };
                    if (!TryPersist(Replace(match, updated)))
                        return false;
                    match = updated;
                }

                resolution = ToResolution(match);
                return true;
            }

            var created = new Entry(
                PlayerProfileId.CreateNew().ToString(),
                canonicalProfileDirectoryPath,
                physicalIdentity);
            if (!TryPersist([.. _entries, created]))
                return false;

            resolution = ToResolution(created);
            return true;
        }
    }

    private bool ReloadCurrentEntries()
    {
        if (_writesBlocked)
            return false;

        try
        {
            var json = File.ReadAllText(_registryFilePath);
            var document = JsonSerializer.Deserialize<Document>(json)
                ?? throw new JsonException("The player identity registry root must be an object, not null.");
            _entries = Validate(document);
            FailureReason = PlayerProfileIdentityRegistryFailureReason.None;
            return true;
        }
        catch (FileNotFoundException)
        {
            _entries = [];
            FailureReason = PlayerProfileIdentityRegistryFailureReason.None;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            _entries = [];
            return true;
        }
        catch (JsonException)
        {
            _writesBlocked = true;
            FailureReason = PlayerProfileIdentityRegistryFailureReason.Invalid;
            return false;
        }
        catch (IOException)
        {
            _writesBlocked = true;
            FailureReason = PlayerProfileIdentityRegistryFailureReason.Unavailable;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            _writesBlocked = true;
            FailureReason = PlayerProfileIdentityRegistryFailureReason.Unavailable;
            return false;
        }
    }

    private static List<Entry> Validate(Document document)
    {
        if (document.Entries is null)
            throw new JsonException("The player identity registry requires an Entries array.");

        var entries = new List<Entry>(document.Entries.Count);
        var profileIds = new HashSet<PlayerProfileId>();
        var physicalIdentities = new HashSet<string>(StringComparer.Ordinal);
        var unconfirmedPaths = new HashSet<string>(WurmDiscoveryPathPolicy.Comparer);
        foreach (var entry in document.Entries)
        {
            if (entry is null
                || !PlayerProfileId.TryParse(entry.Id, out var profileId)
                || string.IsNullOrWhiteSpace(entry.CanonicalProfileDirectoryPath))
            {
                throw new JsonException("The player identity registry contains an invalid entry.");
            }

            if (!profileIds.Add(profileId))
                throw new JsonException("The player identity registry contains duplicate profile IDs.");

            string normalizedPath;
            try
            {
                normalizedPath = WurmDiscoveryPathPolicy.NormalizeDirectoryPath(entry.CanonicalProfileDirectoryPath);
            }
            catch (ArgumentException exception)
            {
                throw new JsonException("The player identity registry contains an invalid path.", exception);
            }
            catch (NotSupportedException exception)
            {
                throw new JsonException("The player identity registry contains an invalid path.", exception);
            }

            if (!WurmDiscoveryPathPolicy.Comparer.Equals(normalizedPath, entry.CanonicalProfileDirectoryPath))
                throw new JsonException("The player identity registry path is not normalized.");

            var physicalIdentity = string.IsNullOrWhiteSpace(entry.PhysicalIdentity) ? null : entry.PhysicalIdentity.Trim();
            if (physicalIdentity is not null && !physicalIdentities.Add(physicalIdentity))
                throw new JsonException("The player identity registry contains duplicate physical evidence.");
            if (physicalIdentity is null && !unconfirmedPaths.Add(normalizedPath))
                throw new JsonException("The player identity registry contains duplicate unconfirmed paths.");

            entries.Add(new Entry(entry.Id, normalizedPath, physicalIdentity));
        }

        return entries;
    }

    private bool TryPersist(List<Entry> candidate)
    {
        if (_writesBlocked)
            return false;

        string? temporaryFilePath = null;
        try
        {
            var directoryPath = Path.GetDirectoryName(_registryFilePath)
                ?? throw new IOException("The player identity registry has no parent directory.");
            Directory.CreateDirectory(directoryPath);
            temporaryFilePath = Path.Combine(directoryPath, $".{RegistryFileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryFilePath, JsonSerializer.Serialize(new Document { Entries = candidate }, SerializationOptions));

            if (File.Exists(_registryFilePath))
            {
                // Re-validate the on-disk document before replacing it. A corrupted file
                // introduced after the initial load is never silently overwritten.
                _ = Validate(JsonSerializer.Deserialize<Document>(File.ReadAllText(_registryFilePath))
                    ?? throw new JsonException("The player identity registry root must be an object, not null."));
                File.Move(temporaryFilePath, _registryFilePath, overwrite: true);
            }
            else
                File.Move(temporaryFilePath, _registryFilePath);

            temporaryFilePath = null;
            _entries = candidate;
            FailureReason = PlayerProfileIdentityRegistryFailureReason.None;
            return true;
        }
        catch (JsonException)
        {
            _writesBlocked = true;
            FailureReason = PlayerProfileIdentityRegistryFailureReason.Invalid;
            return false;
        }
        catch (IOException)
        {
            FailureReason = PlayerProfileIdentityRegistryFailureReason.Unavailable;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            FailureReason = PlayerProfileIdentityRegistryFailureReason.Unavailable;
            return false;
        }
        finally
        {
            if (temporaryFilePath is not null)
            {
                try { File.Delete(temporaryFilePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private List<Entry> Replace(Entry previous, Entry updated) => _entries
        .Select(entry => entry == previous ? updated : entry)
        .ToList();

    private static ResolvedPlayerProfileIdentity ToResolution(Entry entry)
    {
        _ = PlayerProfileId.TryParse(entry.Id, out var id);
        return new ResolvedPlayerProfileIdentity(
            id,
            entry.CanonicalProfileDirectoryPath,
            entry.PhysicalIdentity is null
                ? PlayerProfileIdentityConfidence.UnconfirmedPathIdentity
                : PlayerProfileIdentityConfidence.ConfirmedPhysicalIdentity);
    }

    private sealed class Document
    {
        public List<Entry>? Entries { get; set; }
    }

    private sealed record Entry(string Id, string CanonicalProfileDirectoryPath, string? PhysicalIdentity);

    private static readonly JsonSerializerOptions SerializationOptions = new() { WriteIndented = true };
}
