using WurmMiningCounter.Models;
using System.IO;

namespace WurmMiningCounter.Services;

internal sealed record LogDiscoveryProfileSnapshot(
    string ProfileName,
    string ProfileDirectoryPath,
    string LogsDirectoryPath,
    PlayerProfileId? ProfileId,
    IReadOnlyList<WurmLogFileLocation> Files,
    bool IsCurrent,
    string? IdentityDiagnostic)
{
    internal string LogicalLogsDirectoryPath { get; init; } = LogsDirectoryPath;
    internal string? PhysicalLogsDirectoryKey { get; init; }
}

internal sealed record LogDiscoverySnapshot(
    WurmLogLocationResult Discovery,
    IReadOnlyList<LogDiscoveryProfileSnapshot> Profiles,
    IReadOnlyList<LogDiscoverySourceCandidate> SourceCandidates,
    LogDiscoveryReconciliationPlan Reconciliation)
{
    internal bool IsComplete => Discovery.IsComplete;
}

internal sealed record LogDiscoverySnapshotPublication(long Version, LogDiscoverySnapshot Snapshot);

/// <summary>Passive activation input only; runtime must establish its own owner lease.</summary>
internal sealed record LogDiscoverySourceCandidate(
    string DisplayName,
    string ProfileDirectoryPath,
    string LogsDirectoryPath,
    IReadOnlyList<WurmLogFileLocation> EventFiles,
    IReadOnlyList<WurmLogFileLocation> SkillsFiles,
    IReadOnlyList<WurmLogLocationOrigin> Origins,
    PlayerProfileId? InformationalProfileId,
    bool IsCurrent,
    string ConsolePath,
    string ConsoleWriterEvidence)
{
    /// <summary>
    /// Opaque evidence identifying the physical profile directory. Null means
    /// unknown, never a path-derived substitute.
    /// </summary>
    internal string? PhysicalDirectoryKey { get; init; }
    internal string? LogicalLogsDirectoryPath { get; init; }
    internal string? PhysicalLogsDirectoryKey { get; init; }

    internal bool IsEligibleForFutureActivation => IsCurrent;
}

/// <summary>One physical discovery source in the versioned desired/current model.</summary>
internal sealed record LogDiscoveryCandidateState(
    string PhysicalDirectoryKey,
    long Generation,
    LogDiscoverySourceCandidate Candidate);

/// <summary>
/// A non-destructive request for a future suppression implementation. It is
/// intentionally not a runtime release command.
/// </summary>
internal sealed record LogDiscoverySuppressionIntent(
    string PhysicalDirectoryKey,
    long Generation,
    long SnapshotVersion);

/// <summary>
/// Immutable reconciliation evidence. Incomplete discovery only extends Desired;
/// only a complete snapshot can create suppression intents.
/// </summary>
internal sealed record LogDiscoveryReconciliationPlan(
    long SnapshotVersion,
    bool IsComplete,
    IReadOnlyList<LogDiscoveryCandidateState> Desired,
    IReadOnlyList<LogDiscoveryCandidateState> Current,
    IReadOnlyList<LogDiscoverySuppressionIntent> SuppressionIntents)
{
    internal static readonly LogDiscoveryReconciliationPlan Empty = new(0, false, [], [], []);
}

/// <summary>Passive owner of one coherent manual discovery result. It never acquires source leases.</summary>
internal sealed class LogDiscoverySnapshotService
{
    private readonly WurmLogLocationLocator _locator;
    private readonly PlayerProfileIdentityResolver _identityResolver;
    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private LogDiscoverySnapshot? _last;
    private LogDiscoveryReconciliationPlan _reconciliation = LogDiscoveryReconciliationPlan.Empty;
    private long _version;

    internal LogDiscoverySnapshotService(PlayerProfileIdentityResolver identityResolver)
        : this(new WurmLogLocationLocator(), identityResolver) { }

    internal LogDiscoverySnapshotService(WurmLogLocationLocator locator, PlayerProfileIdentityResolver identityResolver)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
    }

    internal event EventHandler<LogDiscoverySnapshotPublication>? SnapshotPublished;

    internal LogDiscoverySnapshotPublication? GetCurrentSnapshot()
    {
        lock (_stateSync)
            return _last is null ? null : new LogDiscoverySnapshotPublication(_version, _last);
    }

    internal LogDiscoverySnapshot Discover(IEnumerable<KnownLogLocation> known, IEnumerable<string> additional)
    {
        LogDiscoverySnapshotPublication publication;
        _discoveryGate.Wait();
        try
        {
            LogDiscoverySnapshot? prior;
            lock (_stateSync)
                prior = _last;

            var result = _locator.Locate(known, additional);
            // Steam metadata or a library may be unavailable even when other
            // installations were discovered; the scan cannot prove absence.
            if (result.SteamDiagnostics.Any(diagnostic => diagnostic.Issue is SteamDiscoveryIssue.MetadataInvalid
                    or SteamDiscoveryIssue.LibraryUnavailable))
                result = result with { IsComplete = false };
            // A previously observed configured input can be temporarily unavailable.
            // A Missing rejection proves it is still configured, not that its sources disappeared.
            if (prior is not null && prior.SourceCandidates.SelectMany(candidate => candidate.Origins)
                .Any(origin => result.Rejections.Any(rejection => rejection.Reason == WurmLogLocationRejectionReason.Missing
                    && WurmDiscoveryPathPolicy.Comparer.Equals(
                        WurmDiscoveryPathPolicy.NormalizeDirectoryPath(rejection.InputPath),
                        WurmDiscoveryPathPolicy.NormalizeDirectoryPath(origin.InputPath)))))
                result = result with { IsComplete = false };
            var current = result.DataRoots.SelectMany(root => root.Profiles.Select(profile =>
            {
                var profileDirectory = profile.ProfileDirectoryPath;
                var resolved = _identityResolver.TryResolveConfirmedProfile(profileDirectory, out var identity);
                return new LogDiscoveryProfileSnapshot(profile.ProfileName, profileDirectory, profile.LogsDirectoryPath,
                    resolved ? identity!.Id : null, profile.Files, true,
                    resolved ? null : "Identity could not be safely resolved.")
                {
                    LogicalLogsDirectoryPath = profile.LogicalLogsDirectoryPath,
                    PhysicalLogsDirectoryKey = profile.PhysicalLogsDirectoryKey
                };
            })).ToArray();
            var profiles = current.AsEnumerable();
            if (!result.IsComplete && prior is not null)
            {
                var currentPaths = current.Select(profile => profile.ProfileDirectoryPath).ToHashSet(WurmDiscoveryPathPolicy.Comparer);
                profiles = profiles.Concat(prior.Profiles.Where(profile => !currentPaths.Contains(profile.ProfileDirectoryPath))
                    .Select(profile => profile with { IsCurrent = false }));
            }
            var profileSnapshot = profiles.ToArray();
            var candidates = profileSnapshot.Select(profile =>
            {
                var root = result.DataRoots.FirstOrDefault(root => root.Profiles.Any(item =>
                    WurmDiscoveryPathPolicy.Comparer.Equals(item.LogsDirectoryPath, profile.LogsDirectoryPath)));
                var priorCandidate = prior?.SourceCandidates.FirstOrDefault(candidate =>
                    WurmDiscoveryPathPolicy.Comparer.Equals(candidate.ProfileDirectoryPath, profile.ProfileDirectoryPath));
                var physicalDirectoryKey = result.DataRoots.SelectMany(dataRoot => dataRoot.Profiles)
                    .FirstOrDefault(item => WurmDiscoveryPathPolicy.Comparer.Equals(item.LogsDirectoryPath, profile.LogsDirectoryPath))
                    ?.PhysicalDirectoryKey;
                return new LogDiscoverySourceCandidate(profile.ProfileName, profile.ProfileDirectoryPath, profile.LogsDirectoryPath,
                    profile.Files.Where(file => file.Kind == WurmLogFileKind.Event).ToArray(),
                    profile.Files.Where(file => file.Kind == WurmLogFileKind.Skills).ToArray(),
                    root?.Origins ?? priorCandidate?.Origins ?? [], profile.ProfileId, profile.IsCurrent,
                    GetConsolePath(profile.ProfileDirectoryPath, profile.ProfileName),
                    "Active Skills writer still requires a valid client console declaration.")
                {
                    // A current observation without FileId evidence is unknown,
                    // even when a previous path observation had an identity.
                    PhysicalDirectoryKey = physicalDirectoryKey ?? (!profile.IsCurrent ? priorCandidate?.PhysicalDirectoryKey : null),
                    LogicalLogsDirectoryPath = profile.LogicalLogsDirectoryPath,
                    PhysicalLogsDirectoryKey = profile.PhysicalLogsDirectoryKey
                };
            }).ToArray();
            lock (_stateSync)
            {
                var nextVersion = _version + 1;
                _reconciliation = CreateReconciliationPlan(nextVersion,
                    result.IsComplete && result.SourceOwnershipAmbiguities.Count == 0 && result.Uncertainties.Count == 0,
                    candidates, _reconciliation);
                var snapshot = new LogDiscoverySnapshot(result, profileSnapshot, candidates, _reconciliation);
                _last = snapshot;
                publication = new LogDiscoverySnapshotPublication(nextVersion, snapshot);
                _version = nextVersion;
            }
        }
        finally
        {
            _discoveryGate.Release();
        }

        SnapshotPublished?.Invoke(this, publication);
        return publication.Snapshot;
    }

    private static string GetConsolePath(string profileDirectoryPath, string profileName)
    {
        var playersDirectoryPath = Path.GetDirectoryName(profileDirectoryPath);
        var rootDirectoryPath = playersDirectoryPath is null ? null : Path.GetDirectoryName(playersDirectoryPath);
        return rootDirectoryPath is null ? string.Empty : Path.Combine(rootDirectoryPath, $"console.{profileName}.log");
    }

    private static LogDiscoveryReconciliationPlan CreateReconciliationPlan(
        long snapshotVersion,
        bool isComplete,
        IReadOnlyList<LogDiscoverySourceCandidate> candidates,
        LogDiscoveryReconciliationPlan prior)
    {
        var currentCandidates = candidates.Where(candidate => candidate.IsCurrent).ToArray();
        var currentByKey = currentCandidates.Where(candidate => candidate.PhysicalDirectoryKey is not null)
            .GroupBy(candidate => candidate.PhysicalDirectoryKey!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1) // Conflicting claims are deliberately not retirement evidence.
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        // Missing or conflicting physical evidence cannot establish an absence.
        var canPlanRetirement = isComplete && currentCandidates.Length == currentByKey.Count;
        var priorDesired = prior.Desired.ToDictionary(state => state.PhysicalDirectoryKey, StringComparer.Ordinal);
        var desired = canPlanRetirement
            ? new Dictionary<string, LogDiscoveryCandidateState>(StringComparer.Ordinal)
            : new Dictionary<string, LogDiscoveryCandidateState>(priorDesired, StringComparer.Ordinal);

        foreach (var (key, candidate) in currentByKey)
        {
            if (!priorDesired.TryGetValue(key, out var existing))
                existing = new LogDiscoveryCandidateState(key, NextGeneration(key, prior), candidate);
            else
                existing = existing with { Candidate = candidate };
            desired[key] = existing;
        }

        var retainedIntents = canPlanRetirement
            ? prior.SuppressionIntents.Where(intent => !currentByKey.ContainsKey(intent.PhysicalDirectoryKey))
            : prior.SuppressionIntents;
        var intents = canPlanRetirement
            ? retainedIntents
                .Concat(priorDesired.Values.Where(state => !currentByKey.ContainsKey(state.PhysicalDirectoryKey))
                    .Select(state => new LogDiscoverySuppressionIntent(state.PhysicalDirectoryKey, state.Generation, snapshotVersion)))
                .GroupBy(intent => (intent.PhysicalDirectoryKey, intent.Generation))
                .Select(group => group.OrderBy(intent => intent.SnapshotVersion).First())
                .OrderBy(intent => intent.PhysicalDirectoryKey, StringComparer.Ordinal).ToArray()
            : retainedIntents.ToArray();
        var current = currentByKey.Select(pair => desired[pair.Key]).OrderBy(state => state.PhysicalDirectoryKey, StringComparer.Ordinal).ToArray();
        return new LogDiscoveryReconciliationPlan(snapshotVersion, canPlanRetirement,
            desired.Values.OrderBy(state => state.PhysicalDirectoryKey, StringComparer.Ordinal).ToArray(), current, intents);
    }

    private static long NextGeneration(string key, LogDiscoveryReconciliationPlan prior)
    {
        var priorGeneration = prior.Desired.Where(state => string.Equals(state.PhysicalDirectoryKey, key, StringComparison.Ordinal))
            .Select(state => state.Generation)
            .Concat(prior.SuppressionIntents.Where(intent => string.Equals(intent.PhysicalDirectoryKey, key, StringComparison.Ordinal))
                .Select(intent => intent.Generation))
            .DefaultIfEmpty(0).Max();
        return priorGeneration + 1;
    }
}
