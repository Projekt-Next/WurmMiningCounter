using System.IO;
using System.Runtime.ExceptionServices;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed record EventPipelineMetrics(
    long CompletedCycles,
    TimeSpan LastPollDuration,
    TimeSpan LastReadDuration,
    int LastBatchLineCount,
    long ContinuityEvents,
    long SourceReadFaults,
    long OutputFaults);

/// <summary>
/// The first non-retryable fault for one EventPipeline source. The source remains visible
/// for reconciliation but is no longer polled or replayed automatically.
/// </summary>
public sealed record EventPipelineSourceFault(
    PlayerProfileId ProfileId,
    string ProfileName,
    string CanonicalPath,
    long SourceGeneration,
    Exception Exception);

/// <summary>Non-sensitive source identity published for application diagnostics.</summary>
public sealed record EventPipelineSourceStatus(
    PlayerProfileId ProfileId,
    string ProfileName,
    string CanonicalPath,
    long SourceGeneration);

/// <summary>Immutable source checkpoint view for EventPipeline contract tests.</summary>
internal sealed record EventPipelineSourceCheckpoint(
    PlayerProfileId ProfileId,
    string ProfileName,
    string CanonicalPath,
    long ReadOffset,
    string PendingText,
    EventFileFingerprint? Fingerprint,
    bool BaselinePending);

/// <summary>
/// Owns Event file checkpoints. The handoff result explicitly distinguishes no
/// transfer from an indeterminate partial transfer. It must not call back into
/// this pipeline.
/// </summary>
public sealed class EventPipeline : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActiveSegmentGracePeriod = TimeSpan.FromMinutes(5);

    private readonly string _playersDirectoryPath;
    private readonly Func<EventPipelineOutput, EventPipelineHandoffResult> _handoff;
    private readonly Action<PlayerProfileId, string>? _releaseProfileWithIdentity;
    // Used only by controlled retirement. Natural disappearance retains its
    // existing fire-and-forget Action contract.
    private readonly Func<PlayerProfileId, string, Task>? _releaseRetiredProfileAsync;
    private readonly PlayerProfileIdentityResolver _profileIdentityResolver;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, IReadOnlyList<EventLogFileCandidate>> _scan;
    // Candidate snapshots are immutable and may be extended by discovery.
    // Polling only reads the current array; it never runs
    // root discovery or mutates the published snapshot.
    private LogDiscoverySourceCandidate[]? _sourceCandidates;
    private long _sourceCandidateSnapshotVersion;
    private long _conflictSnapshotVersion;
    private readonly HashSet<string> _pendingConflictDirectories = new(WurmDiscoveryPathPolicy.Comparer);
    private readonly HashSet<string> _pendingConflictFiles = new(WurmDiscoveryPathPolicy.Comparer);
    private readonly HashSet<string> _conflictDirectories = new(WurmDiscoveryPathPolicy.Comparer);
    private readonly HashSet<string> _conflictFiles = new(WurmDiscoveryPathPolicy.Comparer);
    private readonly HashSet<string> _resumedConflictFiles = new(WurmDiscoveryPathPolicy.Comparer);
    private LogDiscoverySourceCandidate[]? _pendingConfirmedCandidates;
    private LogDiscoverySourceCandidate[]? _confirmedTransferCandidates;
    private bool _hasPendingDiscoverySnapshot;
    private readonly EventLogReconciliationScanner? _sourceCandidateScanner;
    private readonly Func<string, long> _getLength;
    private readonly Func<string, (long Position, EventFileFingerprint Fingerprint)> _captureSnapshot;
    private readonly Func<string, long, long, EventFileFingerprint?, string, EventLogReadResult> _read;
    private readonly Func<string, long, EventFileFingerprint, EventCheckpointVerificationResult> _verify;
    private readonly Action<string>? _logsBindingCheck;
    private readonly EventLogReader _reader;
    private readonly Action<long>? _cycleCompleted;
    private readonly SortedDictionary<string, EventFileState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ControlledRetirement> _controlledRetirements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NaturalProfileRelease> _naturalProfileReleases = new(StringComparer.Ordinal);
    private readonly object _dispatcherTerminalFaultSync = new();
    private readonly BoundedDispatcherFaultCorrelationCache _dispatcherTerminalFaults = new();
    private FastEventDispatcher? _dispatcherForTerminalFaults;
    private Action<EventModuleTerminalStatus>? _dispatcherTerminalFaultHandler;
    private readonly HashSet<PlayerProfileId> _pendingOwnerDiscoveryBaselines = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifecycleSync = new();
    private readonly object _terminalSourceFaultSync = new();
    private readonly BoundedTerminalFaultHistory<EventPipelineSourceFault> _retainedTerminalSourceFaultHistory =
        new(fault => fault.Exception);
    private Task? _loopTask;
    private Task? _stopTask;
    private Task? _disposeTask;
    private TaskCompletionSource<bool>? _outputAdmissionsDrained;
    private DateTime _sessionStartUtc;
    private long _lastDiscoveryTimestamp;
    private long _lastVerificationTimestamp;
    private int _started;
    private int _stopRequested;
    private int _activeOutputAdmissions;
    private EventPipelineMetrics _metrics = new(0, TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, 0);
    private EventPipelineSourceCheckpoint[] _sourceCheckpoints = [];
    private EventPipelineSourceFault[] _terminalSourceFaults = [];

    public EventPipeline(
        string playersDirectoryPath,
        FastEventDispatcher dispatcher)
        : this(playersDirectoryPath, dispatcher.Handoff, new EventLogReader(), TimeProvider.System,
            releaseProfileWithIdentity: (profileId, _) => dispatcher.ReleaseProfile(profileId),
            releaseRetiredProfileAsync: (profileId, _) => dispatcher.ReleaseProfileAsync(profileId))
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        AttachDispatcherTerminalFaults(dispatcher);
    }

    internal EventPipeline(
        string playersDirectoryPath,
        FastEventDispatcher dispatcher,
        PlayerProfileIdentityResolver profileIdentityResolver)
        : this(playersDirectoryPath, dispatcher.Handoff, new EventLogReader(), TimeProvider.System,
            releaseProfileWithIdentity: (profileId, _) => dispatcher.ReleaseProfile(profileId),
            releaseRetiredProfileAsync: (profileId, _) => dispatcher.ReleaseProfileAsync(profileId),
            profileIdentityResolver: profileIdentityResolver)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(profileIdentityResolver);
        AttachDispatcherTerminalFaults(dispatcher);
    }

    internal EventPipeline(
        string playersDirectoryPath,
        FastEventDispatcher dispatcher,
        PlayerProfileIdentityResolver profileIdentityResolver,
        IReadOnlyList<LogDiscoverySourceCandidate> sourceCandidates)
        : this(playersDirectoryPath, dispatcher.Handoff, new EventLogReader(), TimeProvider.System,
            releaseProfileWithIdentity: (profileId, _) => dispatcher.ReleaseProfile(profileId),
            releaseRetiredProfileAsync: (profileId, _) => dispatcher.ReleaseProfileAsync(profileId),
            profileIdentityResolver: profileIdentityResolver, sourceCandidates: sourceCandidates)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(profileIdentityResolver);
        ArgumentNullException.ThrowIfNull(sourceCandidates);
        AttachDispatcherTerminalFaults(dispatcher);
    }

    public EventPipeline(
        string playersDirectoryPath,
        Func<EventPipelineOutput, EventPipelineHandoffResult> handoff)
        : this(playersDirectoryPath, handoff, new EventLogReader(), TimeProvider.System)
    {
    }

    internal EventPipeline(
        string playersDirectoryPath,
        Func<EventPipelineOutput, EventPipelineHandoffResult> handoff,
        EventLogReader reader,
        TimeProvider timeProvider,
        Func<string, IReadOnlyList<EventLogFileCandidate>>? scan = null,
        Func<string, long>? getLength = null,
        Func<string, (long Position, EventFileFingerprint Fingerprint)>? captureSnapshot = null,
        Func<string, long, long, EventFileFingerprint?, string, EventLogReadResult>? read = null,
        Func<string, long, EventFileFingerprint, EventCheckpointVerificationResult>? verify = null,
        Action<long>? cycleCompleted = null,
        Action<PlayerProfileId, string>? releaseProfileWithIdentity = null,
        Func<PlayerProfileId, string, Task>? releaseRetiredProfileAsync = null,
        PlayerProfileIdentityResolver? profileIdentityResolver = null,
        IReadOnlyList<LogDiscoverySourceCandidate>? sourceCandidates = null,
        Action<string>? logsBindingCheck = null)
    {
        _playersDirectoryPath = Path.GetFullPath(playersDirectoryPath);
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        _releaseProfileWithIdentity = releaseProfileWithIdentity;
        _releaseRetiredProfileAsync = releaseRetiredProfileAsync;
        _profileIdentityResolver = profileIdentityResolver ?? new PlayerProfileIdentityResolver();
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _scan = scan ?? new EventLogReconciliationScanner().Scan;
        _sourceCandidates = sourceCandidates?.Where(candidate => candidate.IsCurrent).ToArray();
        _sourceCandidateScanner = sourceCandidates is null ? null : new EventLogReconciliationScanner();
        _getLength = getLength ?? (path => new FileInfo(path).Length);
        _captureSnapshot = captureSnapshot ?? reader.CaptureSnapshot;
        _read = read ?? reader.Read;
        _verify = verify ?? reader.VerifyCheckpoint;
        _logsBindingCheck = logsBindingCheck;
        _cycleCompleted = cycleCompleted;
    }

    public EventPipelineMetrics Metrics => Volatile.Read(ref _metrics);

    /// <summary>Raised after a source becomes known to EventPipeline; observers cannot affect polling.</summary>
    public event Action<EventPipelineSourceStatus>? SourceDiscovered;

    /// <summary>Raised after a source establishes its EOF baseline; observers cannot affect polling.</summary>
    public event Action<EventPipelineSourceStatus>? SourceBaselined;

    /// <summary>Raised once when a source enters its terminal, non-replayed state.</summary>
    public event Action<EventPipelineSourceFault>? TerminalSourceFaulted;

    /// <summary>Terminal source faults retained for this pipeline lifecycle.</summary>
    public IReadOnlyList<EventPipelineSourceFault> TerminalSourceFaults =>
        Volatile.Read(ref _terminalSourceFaults);

    // Tests use this to assert EventPipeline's own checkpoint contract.
    internal IReadOnlyList<EventPipelineSourceCheckpoint> SourceCheckpoints =>
        Volatile.Read(ref _sourceCheckpoints);

    // Tests use this to verify the bounded dispatcher-fault correlation cache.
    internal int DispatcherTerminalFaultCacheCountForTest
    {
        get
        {
            lock (_dispatcherTerminalFaultSync)
                return _dispatcherTerminalFaults.Count;
        }
    }

    internal (int Retained, long Omitted) TerminalSourceFaultHistoryCountForTest
    {
        get
        {
            lock (_terminalSourceFaultSync)
                return (_retainedTerminalSourceFaultHistory.RetainedCount, _retainedTerminalSourceFaultHistory.OmittedCount);
        }
    }

    internal Task Completion => _loopTask ?? Task.CompletedTask;

    internal void RunCycleForTest() => RunCycle();
    internal void DiscoverForTest() => Discover(isStartup: false);

    /// <summary>
    /// Records a controlled-retirement request. State removal is deferred
    /// to the next RunCycle boundary; this method never mutates _states.
    /// </summary>
    internal bool TryRequestControlledRetirement(string physicalDirectoryKey, long generation,
        long snapshotVersion, bool snapshotIsComplete)
    {
        if (!snapshotIsComplete || string.IsNullOrWhiteSpace(physicalDirectoryKey)
            || generation <= 0 || snapshotVersion <= 0)
            return false;

        lock (_lifecycleSync)
        {
            if (_stopRequested != 0 || _releaseRetiredProfileAsync is null || !HasOneCurrentCandidate(physicalDirectoryKey))
                return false;
            if (_controlledRetirements.TryGetValue(physicalDirectoryKey, out var existing))
            {
                if (existing.Generation == generation && existing.SnapshotVersion == snapshotVersion)
                    return true;

                // The source reappeared while its prior FIFO drain was still
                // pending, then disappeared again.  It never became admissible,
                // so advance the logical suppression generation without starting
                // a second release for the same drained profile.
                if (existing.PendingReappearanceGeneration == generation
                    && snapshotVersion > existing.PendingReappearanceSnapshotVersion
                    && Volatile.Read(ref existing.Phase) != ControlledRetirement.Failed)
                {
                    existing.Generation = generation;
                    existing.SnapshotVersion = snapshotVersion;
                    existing.PendingReappearanceGeneration = null;
                    existing.PendingReappearanceSnapshotVersion = 0;
                    return true;
                }

                return false;
            }

            _controlledRetirements.Add(physicalDirectoryKey,
                new ControlledRetirement(physicalDirectoryKey, generation, snapshotVersion));
            return true;
        }
    }

    /// <summary>
    /// Records a proven reappearance. If FIFO drain is still running, the
    /// existing cycle admits it only after that task completed successfully.
    /// </summary>
    internal bool TryPermitControlledReappearance(string physicalDirectoryKey, long retiredGeneration,
        long reappearanceGeneration, long snapshotVersion)
    {
        if (string.IsNullOrWhiteSpace(physicalDirectoryKey) || reappearanceGeneration <= retiredGeneration)
            return false;

        lock (_lifecycleSync)
        {
            if (!_controlledRetirements.TryGetValue(physicalDirectoryKey, out var retirement)
                || retirement.Generation != retiredGeneration || snapshotVersion <= retirement.SnapshotVersion
                || !HasOneCurrentCandidate(physicalDirectoryKey))
                return false;

            var phase = Volatile.Read(ref retirement.Phase);
            if (phase == ControlledRetirement.Failed)
                return false;
            if (phase == ControlledRetirement.Completed)
            {
                _controlledRetirements.Remove(physicalDirectoryKey);
                _naturalProfileReleases.Remove(physicalDirectoryKey);
                return true;
            }

            if (retirement.PendingReappearanceGeneration is long existingGeneration)
                return existingGeneration == reappearanceGeneration
                    && retirement.PendingReappearanceSnapshotVersion == snapshotVersion;

            retirement.PendingReappearanceGeneration = reappearanceGeneration;
            retirement.PendingReappearanceSnapshotVersion = snapshotVersion;
            return true;
        }
    }

    /// <summary>
    /// Add current discovery candidates from a newer shared snapshot.  This is
    /// deliberately add-only: discovery refresh cannot retire an active source.
    /// </summary>
    internal bool AddSourceCandidates(long snapshotVersion, IReadOnlyList<LogDiscoverySourceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (_sourceCandidates is null || snapshotVersion <= Volatile.Read(ref _sourceCandidateSnapshotVersion))
            return false;

        lock (_lifecycleSync)
        {
            if (_stopRequested != 0 || snapshotVersion <= _sourceCandidateSnapshotVersion)
                return false;

            var current = _sourceCandidates ?? [];
            var known = new HashSet<string>(current.Select(candidate => candidate.ProfileDirectoryPath),
                WurmDiscoveryPathPolicy.Comparer);
            var additions = candidates.Where(candidate => candidate.IsCurrent
                    && known.Add(candidate.ProfileDirectoryPath))
                .ToArray();
            _sourceCandidateSnapshotVersion = snapshotVersion;
            if (additions.Length == 0)
                return false;

            Volatile.Write(ref _sourceCandidates, current.Concat(additions).ToArray());
            return true;
        }
    }

    internal void PublishDiscoverySnapshot(long snapshotVersion, IReadOnlyList<LogDiscoverySourceCandidate> candidates,
        IReadOnlyList<WurmLogSourceOwnershipAmbiguity> ambiguities, bool isAuthoritative)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(ambiguities);
        var fileConflicts = candidates.SelectMany(candidate => candidate.EventFiles.Select(file =>
                (candidate.ProfileDirectoryPath, candidate.LogsDirectoryPath, file.FilePath)))
            .GroupBy(item => item.FilePath, WurmDiscoveryPathPolicy.Comparer)
            .Where(group => group.Select(item => item.ProfileDirectoryPath)
                .Distinct(WurmDiscoveryPathPolicy.Comparer).Skip(1).Any()).ToArray();
        var directories = candidates.GroupBy(candidate => candidate.LogsDirectoryPath, WurmDiscoveryPathPolicy.Comparer)
            .Where(group => group.Select(candidate => candidate.ProfileDirectoryPath)
                .Distinct(WurmDiscoveryPathPolicy.Comparer).Skip(1).Any())
            .Select(group => group.Key)
            .Concat(ambiguities
                .SelectMany(ambiguity => ambiguity.Claims)
                .SelectMany(claim => new[] { claim.Profile.LogsDirectoryPath, claim.Profile.LogicalLogsDirectoryPath }))
            .Concat(fileConflicts.SelectMany(group => group.Select(item => item.LogsDirectoryPath)))
            .ToArray();
        var files = fileConflicts.Select(group => group.Key).ToArray();
        lock (_lifecycleSync)
        {
            if (_stopRequested != 0 || snapshotVersion <= _conflictSnapshotVersion)
                return;
            _conflictSnapshotVersion = snapshotVersion;
            _hasPendingDiscoverySnapshot = true;
            _pendingConflictDirectories.UnionWith(directories);
            _pendingConflictFiles.UnionWith(files);
            var current = candidates.Where(candidate => candidate.IsCurrent).ToArray();
            _pendingConfirmedCandidates = isAuthoritative && ambiguities.Count == 0
                && current.All(candidate => !string.IsNullOrWhiteSpace(candidate.PhysicalDirectoryKey))
                && current.Select(candidate => candidate.PhysicalDirectoryKey).Distinct(StringComparer.Ordinal).Count() == current.Length
                ? current : null;
        }
    }

    private void ApplyDiscoveryConflicts(bool verificationDue = false, bool discoveryDue = false)
    {
        bool snapshotChanged;
        lock (_lifecycleSync)
        {
            snapshotChanged = _hasPendingDiscoverySnapshot;
            if (_hasPendingDiscoverySnapshot)
                _confirmedTransferCandidates = _pendingConfirmedCandidates;
            // A resolved file remains resolved until new conflict evidence
            // names that file or its directory. An incomplete snapshot alone
            // cannot undo a prior authoritative decision.
            if (snapshotChanged)
                _resumedConflictFiles.RemoveWhere(path => _pendingConflictFiles.Contains(path)
                    || (Path.GetDirectoryName(path) is string directory && _pendingConflictDirectories.Contains(directory))
                    || (_pendingConfirmedCandidates is null && !_states.ContainsKey(path)));
            _conflictDirectories.UnionWith(_pendingConflictDirectories);
            _conflictFiles.UnionWith(_pendingConflictFiles);
            if (_pendingConflictDirectories.Count != 0 || _pendingConflictFiles.Count != 0)
            {
                // A directory conflict already blocks every file in that directory.
                _conflictFiles.RemoveWhere(path => Path.GetDirectoryName(path) is string directory
                    && _conflictDirectories.Contains(directory));
            }
            _pendingConflictDirectories.Clear();
            _pendingConflictFiles.Clear();
            if (_pendingConfirmedCandidates is { } confirmed)
            {
                foreach (var candidate in confirmed.Where(candidate => !string.IsNullOrEmpty(candidate.PhysicalLogsDirectoryKey)
                    && !string.IsNullOrEmpty(candidate.PhysicalDirectoryKey)))
                {
                    foreach (var previous in (_sourceCandidates ?? []).Where(previous => !string.IsNullOrEmpty(previous.PhysicalDirectoryKey)
                        && string.Equals(previous.PhysicalLogsDirectoryKey, candidate.PhysicalLogsDirectoryKey, StringComparison.Ordinal)
                        && !string.Equals(previous.PhysicalDirectoryKey, candidate.PhysicalDirectoryKey, StringComparison.Ordinal)))
                    {
                        _conflictDirectories.Add(candidate.LogsDirectoryPath);
                        _conflictDirectories.Add(previous.LogsDirectoryPath);
                        _resumedConflictFiles.RemoveWhere(path => WurmDiscoveryPathPolicy.Comparer.Equals(
                            Path.GetDirectoryName(path), candidate.LogsDirectoryPath)
                            || WurmDiscoveryPathPolicy.Comparer.Equals(Path.GetDirectoryName(path), previous.LogsDirectoryPath));
                    }
                }
                foreach (var state in _states.Values.Where(state => IsDiscoveryConflicted(state.CanonicalPath)))
                {
                    var owners = confirmed.Where(candidate =>
                        WurmDiscoveryPathPolicy.Comparer.Equals(candidate.LogsDirectoryPath, Path.GetDirectoryName(state.CanonicalPath))
                        || candidate.EventFiles.Any(file => WurmDiscoveryPathPolicy.Comparer.Equals(file.FilePath, state.CanonicalPath))).ToArray();
                    if (owners.Length == 1 && !state.LogsBindingLost && state.Owner.PhysicalIdentity is not null
                        && string.Equals(owners[0].PhysicalDirectoryKey, state.Owner.PhysicalIdentity, StringComparison.Ordinal)
                        && WurmDiscoveryPathPolicy.Comparer.Equals(owners[0].ProfileDirectoryPath, state.Owner.ProfileDirectoryPath)
                        && WurmDiscoveryPathPolicy.Comparer.Equals(owners[0].LogsDirectoryPath, Path.GetDirectoryName(state.CanonicalPath))
                        && owners[0].EventFiles.Any(file => WurmDiscoveryPathPolicy.Comparer.Equals(file.FilePath, state.CanonicalPath)))
                        _resumedConflictFiles.Add(state.CanonicalPath);
                }
            }
            _pendingConfirmedCandidates = null;
            _hasPendingDiscoverySnapshot = false;
        }
        if (snapshotChanged || verificationDue)
            TryRestoreConfirmedSources(verificationDue);
        if (snapshotChanged || verificationDue || discoveryDue)
            AllowConfirmedNewSegments();
    }

    private void AllowConfirmedNewSegments()
    {
        lock (_lifecycleSync)
        {
            if (_hasPendingDiscoverySnapshot || _confirmedTransferCandidates is null)
                return;
            foreach (var candidate in _confirmedTransferCandidates)
            {
                var owners = _confirmedTransferCandidates.Where(other =>
                    WurmDiscoveryPathPolicy.Comparer.Equals(other.LogsDirectoryPath, candidate.LogsDirectoryPath)).ToArray();
                if (owners.Length != 1 || string.IsNullOrEmpty(candidate.PhysicalLogsDirectoryKey)
                    || string.IsNullOrEmpty(candidate.LogicalLogsDirectoryPath)
                    || IsControlledSuppressedUnsafe(candidate.PhysicalDirectoryKey)
                    || (_sourceCandidates ?? []).Any(previous => !string.IsNullOrEmpty(previous.PhysicalDirectoryKey)
                        && string.Equals(previous.PhysicalLogsDirectoryKey, candidate.PhysicalLogsDirectoryKey, StringComparison.Ordinal)
                        && !string.Equals(previous.PhysicalDirectoryKey, candidate.PhysicalDirectoryKey, StringComparison.Ordinal))
                    || _states.Values.Any(state => WurmDiscoveryPathPolicy.Comparer.Equals(
                            Path.GetDirectoryName(state.CanonicalPath), candidate.LogsDirectoryPath)
                        && (!WurmDiscoveryPathPolicy.Comparer.Equals(state.Owner.ProfileDirectoryPath, candidate.ProfileDirectoryPath)
                            || state.LogsBindingLost || state.BaselinePending)))
                    continue;
                foreach (var file in candidate.EventFiles)
                    if (IsActive(file.FilePath) && HasDiscoveryConflict(file.FilePath) && !_states.ContainsKey(file.FilePath))
                        _resumedConflictFiles.Add(file.FilePath);
            }
        }
    }

    private bool HasDiscoveryConflict(string path) => _conflictFiles.Contains(path)
        || (Path.GetDirectoryName(path) is string directory && _conflictDirectories.Contains(directory));

    private bool IsDiscoveryConflicted(string path) => HasDiscoveryConflict(path) && !_resumedConflictFiles.Contains(path);

    public void Start()
    {
        lock (_lifecycleSync)
        {
            if (_stopRequested != 0)
                throw new InvalidOperationException("The Event source loop has already stopped.");
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("The Event source loop can only be started once.");

            _sessionStartUtc = _timeProvider.GetUtcNow().UtcDateTime;
            ApplyDiscoveryConflicts();
            Discover(isStartup: true);
            PublishSourceCheckpoints();
            // Each existing source has either an EOF baseline or its own pending baseline.
            var pollEpoch = _timeProvider.GetTimestamp();
            _lastDiscoveryTimestamp = pollEpoch;
            _lastVerificationTimestamp = pollEpoch;
            _loopTask = RunAsync(pollEpoch);
        }
    }

    /// <summary>
    /// Closes source admission and waits for any current cycle. It does not
    /// close downstream writers. The target EventPipeline path uses FastEventDispatcher;
    /// StopAndCompleteAsync stops source work before completing module ingress.
    /// </summary>
    public Task StopAsync()
    {
        lock (_lifecycleSync)
        {
            if (_stopTask is not null)
                return _stopTask;

            Volatile.Write(ref _stopRequested, 1);
            _stop.Cancel();
            var outputAdmissionsDrained = _activeOutputAdmissions == 0
                ? Task.CompletedTask
                : (_outputAdmissionsDrained ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _stopTask = StopCoreAsync(_loopTask ?? Task.CompletedTask, outputAdmissionsDrained);
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(Task loop, Task outputAdmissionsDrained)
    {
        try
        {
            await Task.WhenAll(loop, outputAdmissionsDrained).ConfigureAwait(false);
            await AwaitControlledRetirementsAsync().ConfigureAwait(false);
        }
        finally
        {
            foreach (var state in _states.Values)
                state.Owner.Dispose();
        }
    }

    public async Task StopAndCompleteAsync(FastEventDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        Exception? sourceFailure = null;
        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception exception) { sourceFailure = exception; }
        Exception? dispatcherFailure = null;
        try { await dispatcher.CompleteAsync().ConfigureAwait(false); }
        catch (Exception exception) { dispatcherFailure = exception; }
        var terminalSourceFaults = GetRetainedTerminalSourceFaults();
        var omittedTerminalSourceFaults = GetOmittedTerminalSourceFaultSummary();
        if (terminalSourceFaults.Count == 0)
        {
            if (sourceFailure is not null && dispatcherFailure is not null)
                throw new AggregateException(sourceFailure, dispatcherFailure);
            if (sourceFailure is not null) ExceptionDispatchInfo.Capture(sourceFailure).Throw();
            if (dispatcherFailure is not null) ExceptionDispatchInfo.Capture(dispatcherFailure).Throw();
            return;
        }

        var failures = new List<Exception>();
        AddFailures(sourceFailure, failures);
        foreach (var terminalSourceFault in terminalSourceFaults)
            AddFailures(terminalSourceFault.Exception, failures);
        AddFailures(omittedTerminalSourceFaults, failures);
        AddFailures(dispatcherFailure, failures);
        ThrowFailures(failures);
    }

    public Task DisposeAndCompleteAsync(FastEventDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return DisposeAndCompleteCoreAsync(StopAndCompleteAsync(dispatcher));
    }

    private async Task DisposeAndCompleteCoreAsync(Task shutdown)
    {
        Exception? shutdownFailure = null;
        try { await shutdown.ConfigureAwait(false); }
        catch (Exception exception) { shutdownFailure = exception; }

        Exception? disposalFailure = null;
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { disposalFailure = exception; }

        ThrowPreservingFailures(shutdownFailure, disposalFailure);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Exception? stopFailure = null;
        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception exception) { stopFailure = exception; }
        finally
        {
            DetachDispatcherTerminalFaults();
            _stop.Dispose();
        }

        if (stopFailure is not null)
            ExceptionDispatchInfo.Capture(stopFailure).Throw();
    }

    private static void ThrowPreservingFailures(Exception? primary, Exception? cleanup)
    {
        var failures = new List<Exception>();
        AddFailures(primary, failures);
        AddFailures(cleanup, failures);
        ThrowFailures(failures);
    }

    private static void ThrowFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
            return;
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    private static void AddFailures(Exception? failure, List<Exception> target)
    {
        if (failure is null)
            return;

        IEnumerable<Exception> flattened = failure is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : new[] { failure };
        foreach (var exception in flattened)
        {
            if (!target.Any(existing => ReferenceEquals(existing, exception)))
                target.Add(exception);
        }
    }

    private async Task RunAsync(long pollEpoch)
    {
        long nextPollIndex = 1;
        while (!_stop.IsCancellationRequested)
        {
            var deadline = TimeSpan.FromTicks(checked(nextPollIndex * PollInterval.Ticks));
            while (!_stop.IsCancellationRequested)
            {
                var elapsed = _timeProvider.GetElapsedTime(pollEpoch);
                var remaining = deadline - elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;

                try
                {
                    await Task.Delay(remaining, _timeProvider, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
            }

            if (_stop.IsCancellationRequested)
                break;

            RunCycle();
            // Skip every nominal deadline reached during the completed cycle.
            var completedElapsed = _timeProvider.GetElapsedTime(pollEpoch);
            nextPollIndex = checked(completedElapsed.Ticks / PollInterval.Ticks + 1);
        }
    }

    private void RunCycle()
    {
        if (Volatile.Read(ref _stopRequested) != 0)
            return;
        var cycleStart = _timeProvider.GetTimestamp();
        var discoveryDue = _timeProvider.GetElapsedTime(_lastDiscoveryTimestamp, cycleStart) >= DiscoveryInterval;
        var verificationDue = _timeProvider.GetElapsedTime(_lastVerificationTimestamp, cycleStart) >= VerificationInterval;
        ApplyDiscoveryConflicts(verificationDue, discoveryDue);
        ProcessControlledRetirements();

        if (discoveryDue)
        {
            Discover(isStartup: false);
            _lastDiscoveryTimestamp = _timeProvider.GetTimestamp();
        }

        foreach (var state in _states.Values)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
                break;
            PollSource(state, verificationDue);
        }

        if (verificationDue)
            _lastVerificationTimestamp = _timeProvider.GetTimestamp();

        PublishSourceCheckpoints();

        var metrics = _metrics;
        Volatile.Write(ref _metrics, metrics with
        {
            CompletedCycles = metrics.CompletedCycles + 1,
            LastPollDuration = _timeProvider.GetElapsedTime(cycleStart)
        });
        _cycleCompleted?.Invoke(metrics.CompletedCycles + 1);
    }

    private void Discover(bool isStartup)
    {
        IReadOnlyList<EventLogFileCandidate> candidates;
        if (Volatile.Read(ref _sourceCandidates) is not null)
        {
            candidates = ScanSourceCandidates();
        }
        else
        {
            try
            {
                candidates = _scan(_playersDirectoryPath);
            }
            catch (IOException)
            {
                RecordReadFault();
                return;
            }
            catch (UnauthorizedAccessException)
            {
                RecordReadFault();
                return;
            }
        }

        foreach (var state in _states.Values.Where(state => state.OwnerRebindPending && state.TerminalFault is null
                     && !IsDiscoveryConflicted(state.CanonicalPath)).ToArray())
        {
            try { TryRebindOwner(state); }
            catch (EventPipelineHandoffIndeterminateException exception) { RecordTerminalSourceFault(state, exception); }
        }

        var removedProfiles = new Dictionary<PlayerProfileId, NaturalProfileRelease>();
        foreach (var path in _states.Keys.Where(path => !IsActive(path) && !IsDiscoveryConflicted(path)).ToArray())
        {
            var state = _states[path];
            removedProfiles[state.ProfileId] = new NaturalProfileRelease(state.Owner.PhysicalIdentity,
                new EventPipelineSourceStatus(state.ProfileId, state.ProfileName, state.CanonicalPath, state.Generation), null);
            state.Owner.Dispose();
            _states.Remove(path);
            if (!state.LogsBindingLost && !state.OwnerRebindPending && !state.BaselinePending
                && !state.DisappearancePending && state.TerminalFault is null)
                _resumedConflictFiles.Remove(path);
        }

        var rebaselineProfiles = new HashSet<PlayerProfileId>(_pendingOwnerDiscoveryBaselines);
        foreach (var candidate in candidates)
        {
            if (!IsActive(candidate.EventFilePath) || _states.ContainsKey(candidate.EventFilePath))
                continue;

            var sourceCandidate = FindSourceCandidate(candidate);
            var confirmedCandidates = _confirmedTransferCandidates;
            var lateConflictFile = IsDiscoveryConflicted(candidate.EventFilePath);
            if (lateConflictFile)
            {
                var matchingOwners = confirmedCandidates?.Where(source => WurmDiscoveryPathPolicy.Comparer.Equals(
                    source.LogsDirectoryPath, Path.GetDirectoryName(candidate.EventFilePath))).ToArray();
                if (sourceCandidate is null || matchingOwners is not { Length: 1 }
                    || !WurmDiscoveryPathPolicy.Comparer.Equals(matchingOwners[0].ProfileDirectoryPath, sourceCandidate.ProfileDirectoryPath)
                    || !WurmDiscoveryPathPolicy.Comparer.Equals(matchingOwners[0].LogicalLogsDirectoryPath, sourceCandidate.LogicalLogsDirectoryPath)
                    || string.IsNullOrEmpty(matchingOwners[0].PhysicalDirectoryKey)
                    || string.IsNullOrEmpty(matchingOwners[0].PhysicalLogsDirectoryKey)
                    || string.IsNullOrEmpty(matchingOwners[0].LogicalLogsDirectoryPath)
                    || !string.Equals(matchingOwners[0].PhysicalDirectoryKey, sourceCandidate.PhysicalDirectoryKey, StringComparison.Ordinal)
                    || !string.Equals(matchingOwners[0].PhysicalLogsDirectoryKey, sourceCandidate.PhysicalLogsDirectoryKey, StringComparison.Ordinal)
                    || (_sourceCandidates ?? []).Any(previous => !string.IsNullOrEmpty(previous.PhysicalDirectoryKey)
                        && string.Equals(previous.PhysicalLogsDirectoryKey, sourceCandidate.PhysicalLogsDirectoryKey, StringComparison.Ordinal)
                        && !string.Equals(previous.PhysicalDirectoryKey, sourceCandidate.PhysicalDirectoryKey, StringComparison.Ordinal))
                    || _states.Values.Any(state => WurmDiscoveryPathPolicy.Comparer.Equals(Path.GetDirectoryName(state.CanonicalPath), sourceCandidate.LogsDirectoryPath)
                        && (!WurmDiscoveryPathPolicy.Comparer.Equals(state.Owner.ProfileDirectoryPath, sourceCandidate.ProfileDirectoryPath)
                            || state.LogsBindingLost || state.BaselinePending)))
                    continue;
            }

            if (!_profileIdentityResolver.TryRegisterSource(GetProfileDirectoryPath(candidate), out var owner, out var readGuard)
                || owner is null)
                continue;

            using (readGuard)
            {
                if (sourceCandidate?.LogicalLogsDirectoryPath is not null
                    && (string.IsNullOrEmpty(sourceCandidate.PhysicalDirectoryKey)
                        || !string.Equals(owner.PhysicalIdentity, sourceCandidate.PhysicalDirectoryKey, StringComparison.Ordinal)))
                {
                    owner.Dispose();
                    continue;
                }
                if (owner.RequiresEofBaseline || _states.Values.Any(existing => existing.ProfileId != owner.Identity.Id
                    && string.Equals(existing.Owner.ProfileDirectoryPath, owner.ProfileDirectoryPath, StringComparison.OrdinalIgnoreCase)))
                    rebaselineProfiles.Add(owner.Identity.Id);
                var state = new EventFileState
                {
                    Owner = owner,
                    ProfileName = candidate.ProfileName,
                    CanonicalPath = candidate.EventFilePath,
                    LogicalLogsDirectoryPath = sourceCandidate?.LogicalLogsDirectoryPath,
                    PhysicalLogsDirectoryKey = sourceCandidate?.PhysicalLogsDirectoryKey,
                    BaselinePending = isStartup || rebaselineProfiles.Contains(owner.Identity.Id) || candidate.CreationTimeUtc < _sessionStartUtc
                        || (HasDiscoveryConflict(candidate.EventFilePath) && sourceCandidate is not null
                            && Volatile.Read(ref _sourceCandidates)?.Any(source =>
                                WurmDiscoveryPathPolicy.Comparer.Equals(source.LogsDirectoryPath, sourceCandidate.LogsDirectoryPath)
                                && !WurmDiscoveryPathPolicy.Comparer.Equals(source.ProfileDirectoryPath, sourceCandidate.ProfileDirectoryPath)) == true)
                };
                var bindingVerified = VerifyLogsBinding(state);
                if (lateConflictFile && !bindingVerified)
                {
                    owner.Dispose();
                    continue;
                }
                // The request can arrive while this normal discovery pass is
                // scanning. Linearize its suppression check with state
                // admission so an accepted request cannot admit a new state.
                var suppressed = false;
                lock (_lifecycleSync)
                {
                    suppressed = IsControlledSuppressedUnsafe(owner.PhysicalIdentity)
                        || (lateConflictFile && (_hasPendingDiscoverySnapshot || !ReferenceEquals(confirmedCandidates, _confirmedTransferCandidates)));
                    if (!suppressed)
                    {
                        if (lateConflictFile)
                            _resumedConflictFiles.Add(state.CanonicalPath);
                        _states.Add(state.CanonicalPath, state);
                        if (owner.PhysicalIdentity is string physicalDirectoryKey)
                            _naturalProfileReleases.Remove(physicalDirectoryKey);
                    }
                }
                if (suppressed)
                {
                    owner.Dispose();
                    continue;
                }
                Notify(SourceDiscovered, new EventPipelineSourceStatus(
                    state.ProfileId, state.ProfileName, state.CanonicalPath, state.Generation));
                if (state.BaselinePending && VerifyLogsBinding(state))
                    TryBaselineCore(state);
            }
        }

        foreach (var removedProfile in removedProfiles.Values)
        {
            if (!_states.Values.Any(state => state.ProfileId == removedProfile.ProfileId))
                ReleaseProfileNaturally(removedProfile.PhysicalDirectoryKey, removedProfile.Source);
        }

        _pendingOwnerDiscoveryBaselines.ExceptWith(rebaselineProfiles);

        PublishTerminalSourceFaults();
    }

    // Runs only on the pipeline cycle thread, before discovery and polling.
    // Requests are recorded under _lifecycleSync, but _states stay exclusively
    // owned by this cycle boundary.
    private void ProcessControlledRetirements()
    {
        ControlledRetirement[] retirements;
        lock (_lifecycleSync)
            retirements = _controlledRetirements.Values.ToArray();

        foreach (var retirement in retirements)
        {
            if (_states.Values.Any(state => IsDiscoveryConflicted(state.CanonicalPath)
                && string.Equals(state.Owner.PhysicalIdentity, retirement.PhysicalDirectoryKey, StringComparison.Ordinal)
                && (_confirmedTransferCandidates is null || _confirmedTransferCandidates.Any(candidate =>
                    WurmDiscoveryPathPolicy.Comparer.Equals(candidate.LogsDirectoryPath, Path.GetDirectoryName(state.CanonicalPath))
                    || candidate.EventFiles.Any(file => WurmDiscoveryPathPolicy.Comparer.Equals(file.FilePath, state.CanonicalPath))))))
                continue;
            if (retirement.ReleaseTask is not null)
            {
                if (retirement.ReleaseTask.IsCompletedSuccessfully)
                {
                    Volatile.Write(ref retirement.Phase, ControlledRetirement.Completed);
                    lock (_lifecycleSync)
                    {
                        if (_controlledRetirements.TryGetValue(retirement.PhysicalDirectoryKey, out var current)
                            && ReferenceEquals(current, retirement)
                            && retirement.PendingReappearanceGeneration is not null)
                        {
                            _controlledRetirements.Remove(retirement.PhysicalDirectoryKey);
                            _naturalProfileReleases.Remove(retirement.PhysicalDirectoryKey);
                        }
                    }
                }
                else if (retirement.ReleaseTask.IsFaulted || retirement.ReleaseTask.IsCanceled)
                {
                    Volatile.Write(ref retirement.Phase, ControlledRetirement.Failed);
                    RecordControlledRetirementFailure(retirement, GetRetirementReleaseFailure(retirement.ReleaseTask));
                }
                continue;
            }
            if (Volatile.Read(ref retirement.Phase) != ControlledRetirement.Requested)
                continue;

            var matched = _states.Values.Where(state => string.Equals(state.Owner.PhysicalIdentity,
                retirement.PhysicalDirectoryKey, StringComparison.Ordinal)).ToArray();
            if (matched.Length == 0)
            {
                NaturalProfileRelease? naturalRelease;
                lock (_lifecycleSync)
                    _naturalProfileReleases.TryGetValue(retirement.PhysicalDirectoryKey, out naturalRelease);
                if (naturalRelease?.ReleaseTask is null)
                {
                    Volatile.Write(ref retirement.Phase, ControlledRetirement.Failed);
                    continue;
                }

                // FastEventDispatcher.ReleaseProfileAsync returns the same
                // in-flight Task started by its natural Action release.
                retirement.ReleaseTask = naturalRelease.ReleaseTask;
                retirement.ReleaseProfileId = naturalRelease.ProfileId;
                retirement.Source = naturalRelease.Source;
                Volatile.Write(ref retirement.Phase, ControlledRetirement.Releasing);
                continue;
            }

            var firstMatched = matched[0];
            retirement.Source = new EventPipelineSourceStatus(firstMatched.ProfileId, firstMatched.ProfileName,
                firstMatched.CanonicalPath, firstMatched.Generation);

            var affectedProfiles = matched.Select(state => (state.ProfileId, state.ProfileName)).Distinct().ToArray();
            foreach (var state in matched)
            {
                _states.Remove(state.CanonicalPath);
                _resumedConflictFiles.Remove(state.CanonicalPath);
                state.Owner.Dispose();
            }

            foreach (var (profileId, profileName) in affectedProfiles)
            {
                if (_states.Values.Any(state => state.ProfileId == profileId))
                    continue;

                try
                {
                    retirement.ReleaseTask = _releaseRetiredProfileAsync!(profileId, profileName);
                    retirement.ReleaseProfileId = profileId;
                    Volatile.Write(ref retirement.Phase, ControlledRetirement.Releasing);
                    break;
                }
                catch (Exception exception)
                {
                    retirement.ReleaseTask = Task.FromException(exception);
                    Volatile.Write(ref retirement.Phase, ControlledRetirement.Failed);
                    RecordControlledRetirementFailure(retirement, exception);
                    break;
                }
            }

            // A profile retaining another Event state needs no dispatcher
            // release; no old FIFO is being retired for this physical source.
            if (retirement.ReleaseTask is null)
                Volatile.Write(ref retirement.Phase, ControlledRetirement.Completed);
        }
    }

    private async Task AwaitControlledRetirementsAsync()
    {
        Task[] releases;
        lock (_lifecycleSync)
            releases = _controlledRetirements.Values
                .Select(retirement => retirement.ReleaseTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
        if (releases.Length != 0)
            await Task.WhenAll(releases).ConfigureAwait(false);
    }

    private bool IsActive(string path)
    {
        var segment = EventLogSegmentOrder.Parse(path);
        if (segment.Format == EventLogSegmentFormat.Unknown)
            return false;

        var localNow = _timeProvider.GetLocalNow().DateTime;
        var cutoff = localNow - ActiveSegmentGracePeriod;
        return segment.Start.ToDateTime(TimeOnly.MinValue) <= localNow
            && segment.EndExclusive.ToDateTime(TimeOnly.MinValue) > cutoff;
    }

    private void TryBaseline(EventFileState state)
    {
        var ownership = state.Owner.TryOpenRead(out var readGuard);
        using (readGuard)
        {
            if (ownership == ProfileSourceOwnershipStatus.Changed)
                TryRebindOwner(state);
            else if (ownership == ProfileSourceOwnershipStatus.Verified && VerifyLogsBinding(state))
                TryBaselineCore(state);
            else
                RecordReadFault();
        }
    }

    private void TryBaselineCore(EventFileState state)
    {
        try
        {
            var snapshot = _captureSnapshot(state.CanonicalPath);
            var signature = _reader.CaptureContinuitySignature(
                state.CanonicalPath, snapshot.Position, snapshot.Fingerprint);
            if (signature is null)
            {
                RecordReadFault();
                return;
            }
            state.ReadOffset = snapshot.Position;
            state.Fingerprint = snapshot.Fingerprint;
            state.ContinuitySignature = signature;
            state.PendingText = string.Empty;
            state.BaselinePending = false;
            state.Missing = false;
            state.VerificationPending = false;
            state.Owner.MarkActivated();
            Notify(SourceBaselined, new EventPipelineSourceStatus(
                state.ProfileId, state.ProfileName, state.CanonicalPath, state.Generation));
        }
        catch (IOException)
        {
            RecordReadFault();
        }
        catch (UnauthorizedAccessException)
        {
            RecordReadFault();
        }
    }

    private void PollSource(EventFileState state, bool verificationDue)
    {
        if (IsDiscoveryConflicted(state.CanonicalPath))
        {
            if (!state.ConflictStatusReported && !state.LogsBindingLost)
            {
                state.Owner.MarkOwnershipConflict();
                state.ConflictStatusReported = true;
            }
            return;
        }
        if (state.ConflictStatusReported)
        {
            state.ConflictStatusReported = false;
            if (!state.LogsBindingLost && !state.BaselinePending && !state.OwnerRebindPending && state.TerminalFault is null)
                state.Owner.MarkActivated();
        }
        if (Volatile.Read(ref _stopRequested) != 0)
            return;
        if (state.TerminalFault is not null)
            return;
        if (state.OwnerRebindPending || state.LogsBindingLost)
            return;

        try
        {
            PollSourceCore(state, verificationDue);
        }
        catch (EventLogOversizedLineException exception)
        {
            RecordTerminalSourceFault(state, exception);
        }
        catch (EventPipelineHandoffIndeterminateException exception)
        {
            RecordTerminalSourceFault(state, exception);
        }
    }

    private void PollSourceCore(EventFileState state, bool verificationDue)
    {
        using var outputAdmission = TryBeginOutputAdmission();
        if (outputAdmission is null)
            return;
        if (state.BaselinePending)
        {
            TryBaseline(state);
            return;
        }

        if (state.DisappearancePending)
        {
            AcceptDisappearance(state);
            return;
        }

        long length;
        try
        {
            length = _getLength(state.CanonicalPath);
        }
        catch (FileNotFoundException) when (!File.Exists(state.CanonicalPath))
        {
            AcceptDisappearance(state);
            return;
        }
        catch (DirectoryNotFoundException) when (!File.Exists(state.CanonicalPath))
        {
            AcceptDisappearance(state);
            return;
        }
        catch (IOException)
        {
            RecordReadFault();
            return;
        }
        catch (UnauthorizedAccessException)
        {
            RecordReadFault();
            return;
        }

        if (!state.Missing && state.ContinuitySignature is not null && length == state.ReadOffset
            && !verificationDue && !state.VerificationPending)
            return;

        var ownership = state.Owner.TryOpenRead(out var readGuard);
        using var sourceReadGuard = readGuard;
        if (ownership == ProfileSourceOwnershipStatus.Changed)
        {
            TryRebindOwner(state);
            return;
        }
        if (ownership != ProfileSourceOwnershipStatus.Verified)
        {
            RecordReadFault();
            return;
        }

        if (!VerifyLogsBinding(state))
            return;

        state.Owner.MarkActivated();

        if (state.Missing)
        {
            TryBaselineCore(state);
            return;
        }

        if (state.ContinuitySignature is null)
        {
            try
            {
                var initialFingerprint = new EventFileFingerprint(0, null);
                var signature = _reader.CaptureContinuitySignature(
                    state.CanonicalPath, 0, initialFingerprint);
                if (signature is null)
                {
                    RecordReadFault();
                    return;
                }
                state.Fingerprint = initialFingerprint;
                state.ContinuitySignature = signature;
            }
            catch (IOException) { RecordReadFault(); return; }
            catch (UnauthorizedAccessException) { RecordReadFault(); return; }
        }

        if (length < state.ReadOffset
            || (length == state.ReadOffset && (verificationDue || state.VerificationPending)))
        {
            Verify(state);
            return;
        }

        if (length == state.ReadOffset)
            return;

        var readStart = _timeProvider.GetTimestamp();
        EventLogReadResult result;
        try
        {
            result = _read(state.CanonicalPath, state.ReadOffset, length, state.Fingerprint, state.PendingText);
        }
        catch (IOException)
        {
            RecordReadFault();
            return;
        }
        catch (UnauthorizedAccessException)
        {
            RecordReadFault();
            return;
        }
        finally
        {
            Volatile.Write(ref _metrics, _metrics with { LastReadDuration = _timeProvider.GetElapsedTime(readStart) });
        }

        if (result.RequiresRetry)
        {
            RecordReadFault();
            return;
        }

        if (result.ContinuityLossReason is { } reason)
        {
            try
            {
                var signature = _reader.CaptureContinuitySignature(
                    state.CanonicalPath, result.NewPosition, result.Fingerprint!);
                if (signature is null)
                {
                    RecordReadFault();
                    return;
                }
                AcceptContinuity(state, reason, result.NewPosition, result.Fingerprint!, signature);
            }
            catch (IOException) { RecordReadFault(); }
            catch (UnauthorizedAccessException) { RecordReadFault(); }
            return;
        }

        EventFileContinuitySignature? newSignature;
        try
        {
            var currentSignature = _reader.CaptureContinuitySignature(
                state.CanonicalPath, state.ReadOffset, state.Fingerprint!);
            if (currentSignature != state.ContinuitySignature)
            {
                Verify(state);
                return;
            }

            newSignature = _reader.CaptureContinuitySignature(
                state.CanonicalPath, result.NewPosition, result.Fingerprint!);
        }
        catch (IOException) { RecordReadFault(); return; }
        catch (UnauthorizedAccessException) { RecordReadFault(); return; }
        if (newSignature is null)
        {
            RecordReadFault();
            return;
        }

        if (result.Lines.Count > 0)
        {
            var batch = new EventReadBatch(
                state.ProfileId,
                state.ProfileName,
                state.CanonicalPath,
                state.Generation,
                result.Lines);
            if (Handoff(batch) != EventPipelineHandoffResult.Complete)
                return;
            Volatile.Write(ref _metrics, _metrics with { LastBatchLineCount = batch.Lines.Count });
        }
        else
        {
            Volatile.Write(ref _metrics, _metrics with { LastBatchLineCount = 0 });
        }

        Commit(state, result.NewPosition, result.Fingerprint, newSignature, result.PendingText);
        state.VerificationPending = false;
    }

    private void Verify(EventFileState state)
    {
        if (state.Fingerprint is null)
            return;

        EventCheckpointVerificationResult result;
        try
        {
            result = _verify(state.CanonicalPath, state.ReadOffset, state.Fingerprint);
        }
        catch (IOException)
        {
            state.VerificationPending = true;
            RecordReadFault();
            return;
        }
        catch (UnauthorizedAccessException)
        {
            state.VerificationPending = true;
            RecordReadFault();
            return;
        }

        if (result == EventCheckpointVerificationResult.Valid)
        {
            try
            {
                var signature = _reader.CaptureContinuitySignature(
                    state.CanonicalPath, state.ReadOffset, state.Fingerprint);
                if (signature is null)
                {
                    state.VerificationPending = true;
                    RecordReadFault();
                    return;
                }
                if (signature == state.ContinuitySignature)
                {
                    state.VerificationPending = false;
                    return;
                }
                result = EventCheckpointVerificationResult.Replaced;
            }
            catch (IOException) { state.VerificationPending = true; RecordReadFault(); return; }
            catch (UnauthorizedAccessException) { state.VerificationPending = true; RecordReadFault(); return; }
        }
        if (result == EventCheckpointVerificationResult.RequiresRetry)
        {
            state.VerificationPending = true;
            RecordReadFault();
            return;
        }

        state.VerificationPending = true;
        try
        {
            var snapshot = _captureSnapshot(state.CanonicalPath);
            var signature = _reader.CaptureContinuitySignature(
                state.CanonicalPath, snapshot.Position, snapshot.Fingerprint);
            if (signature is null)
            {
                RecordReadFault();
                return;
            }
            AcceptContinuity(
                state,
                result == EventCheckpointVerificationResult.Truncated
                    ? StreamGapReason.Truncated
                    : StreamGapReason.Replaced,
                snapshot.Position,
                snapshot.Fingerprint,
                signature);
        }
        catch (IOException)
        {
            RecordReadFault();
        }
        catch (UnauthorizedAccessException)
        {
            RecordReadFault();
        }
    }

    private void AcceptContinuity(
        EventFileState state,
        StreamGapReason reason,
        long safeOffset,
        EventFileFingerprint fingerprint,
        EventFileContinuitySignature signature)
    {
        var nextGeneration = checked(state.Generation + 1);
        state.VerificationPending = true;
        if (Handoff(new EventContinuityLoss(state.ProfileId,
            state.ProfileName, state.CanonicalPath, nextGeneration, reason))
            != EventPipelineHandoffResult.Complete)
            return;

        state.Generation = nextGeneration;
        Commit(state, safeOffset, fingerprint, signature, string.Empty);
        state.VerificationPending = false;
        Volatile.Write(ref _metrics, _metrics with { ContinuityEvents = _metrics.ContinuityEvents + 1 });
    }

    private void AcceptDisappearance(EventFileState state)
    {
        if (state.Missing)
            return;

        var ownership = state.Owner.TryOpenRead(out var readGuard);
        using var sourceReadGuard = readGuard;
        if (ownership != ProfileSourceOwnershipStatus.Verified || !VerifyLogsBinding(state))
            return;

        state.DisappearancePending = true;
        var nextGeneration = checked(state.Generation + 1);
        if (Handoff(new EventContinuityLoss(state.ProfileId,
            state.ProfileName, state.CanonicalPath, nextGeneration,
            StreamGapReason.UnrecoverableReadGap))
            != EventPipelineHandoffResult.Complete)
            return;

        state.Generation = nextGeneration;
        state.PendingText = string.Empty;
        state.Missing = true;
        state.DisappearancePending = false;
        Volatile.Write(ref _metrics, _metrics with { ContinuityEvents = _metrics.ContinuityEvents + 1 });
    }

    private EventPipelineHandoffResult Handoff(EventPipelineOutput output)
    {
        try
        {
            var result = _handoff(output);
            if (result == EventPipelineHandoffResult.Complete)
                return result;

            RecordOutputFault();
            if (result == EventPipelineHandoffResult.NotStarted)
                return result;

            if (result == EventPipelineHandoffResult.Indeterminate)
                throw new EventPipelineHandoffIndeterminateException(output);

            throw new InvalidOperationException("Unknown Event Pipeline handoff result.");
        }
        catch (EventPipelineHandoffIndeterminateException)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordOutputFault();
            throw new EventPipelineHandoffIndeterminateException(output, exception);
        }
    }

    private OutputAdmission? TryBeginOutputAdmission()
    {
        lock (_lifecycleSync)
        {
            if (_stopRequested != 0)
                return null;

            _activeOutputAdmissions++;
        }
        return new OutputAdmission(this);
    }

    private void EndOutputAdmission()
    {
        lock (_lifecycleSync)
        {
            _activeOutputAdmissions--;
            if (_stopRequested != 0 && _activeOutputAdmissions == 0)
                _outputAdmissionsDrained?.TrySetResult(true);
        }
    }

    private static void Commit(
        EventFileState state,
        long offset,
        EventFileFingerprint? fingerprint,
        EventFileContinuitySignature signature,
        string pendingText)
    {
        state.ReadOffset = offset;
        state.Fingerprint = fingerprint;
        state.ContinuitySignature = signature;
        state.PendingText = pendingText;
    }

    private void RecordReadFault() =>
        Volatile.Write(ref _metrics, _metrics with { SourceReadFaults = _metrics.SourceReadFaults + 1 });

    private void RecordTerminalSourceFault(EventFileState state, Exception exception)
    {
        if (state.TerminalFault is not null)
            return;

        state.TerminalFault = exception;
        var fault = new EventPipelineSourceFault(
            state.ProfileId, state.ProfileName, state.CanonicalPath, state.Generation, exception);
        lock (_terminalSourceFaultSync)
        {
            _retainedTerminalSourceFaultHistory.Add(fault);
            Volatile.Write(ref _terminalSourceFaults, _retainedTerminalSourceFaultHistory.Snapshot().ToArray());
        }
        RecordReadFault();
        Notify(TerminalSourceFaulted, fault);
    }

    private static void Notify<T>(Action<T>? observers, T value)
    {
        if (observers is null)
            return;

        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try { observer(value); }
            catch { /* App diagnostics must not affect source polling. */ }
        }
    }

    private IReadOnlyList<EventPipelineSourceFault> GetRetainedTerminalSourceFaults()
    {
        lock (_terminalSourceFaultSync)
            return _retainedTerminalSourceFaultHistory.Snapshot();
    }

    private Exception? GetOmittedTerminalSourceFaultSummary()
    {
        lock (_terminalSourceFaultSync)
            return _retainedTerminalSourceFaultHistory.CreateOmittedSummary("EventPipeline source");
    }

    private void PublishTerminalSourceFaults()
    {
        lock (_terminalSourceFaultSync)
            Volatile.Write(ref _terminalSourceFaults, _retainedTerminalSourceFaultHistory.Snapshot().ToArray());
    }

    private void RecordOutputFault() =>
        Volatile.Write(ref _metrics, _metrics with { OutputFaults = _metrics.OutputFaults + 1 });

    private void PublishSourceCheckpoints() =>
        Volatile.Write(ref _sourceCheckpoints, _states.Values.Select(state =>
            new EventPipelineSourceCheckpoint(
                state.ProfileId,
                state.ProfileName,
                state.CanonicalPath,
                state.ReadOffset,
                state.PendingText,
                state.Fingerprint,
                state.BaselinePending)).ToArray());

    private static string GetProfileDirectoryPath(EventLogFileCandidate candidate)
    {
        var profileDirectoryPath = candidate.ProfileDirectoryPath;
        if (string.IsNullOrWhiteSpace(profileDirectoryPath))
        {
            var logsDirectoryPath = Path.GetDirectoryName(candidate.EventFilePath);
            profileDirectoryPath = logsDirectoryPath is null ? candidate.EventFilePath : Path.GetDirectoryName(logsDirectoryPath) ?? logsDirectoryPath;
        }

        return profileDirectoryPath;
    }

    private LogDiscoverySourceCandidate? FindSourceCandidate(EventLogFileCandidate file) =>
        Volatile.Read(ref _sourceCandidates)?.FirstOrDefault(candidate =>
            WurmDiscoveryPathPolicy.Comparer.Equals(candidate.ProfileDirectoryPath, GetProfileDirectoryPath(file))
            && WurmDiscoveryPathPolicy.Comparer.Equals(candidate.LogsDirectoryPath, Path.GetDirectoryName(file.EventFilePath)));

    private bool VerifyLogsBinding(EventFileState state)
    {
        // Legacy scanner callers have no discovery binding evidence.
        if (state.LogicalLogsDirectoryPath is null)
            return true;
        _logsBindingCheck?.Invoke(state.LogicalLogsDirectoryPath);
        using var guard = WindowsDirectoryPhysicalIdentityProvider.TryOpenReadGuard(state.LogicalLogsDirectoryPath);
        var identities = new WindowsDirectoryPhysicalIdentityProvider();
        var logicalId = guard is null ? null : identities.GetIdentity(state.LogicalLogsDirectoryPath);
        var physicalId = identities.GetIdentity(Path.GetDirectoryName(state.CanonicalPath)!);
        if (guard is null || !guard.IsAvailable || string.IsNullOrEmpty(state.PhysicalLogsDirectoryKey)
            || !string.Equals(logicalId, state.PhysicalLogsDirectoryKey, StringComparison.Ordinal)
            || !string.Equals(physicalId, state.PhysicalLogsDirectoryKey, StringComparison.Ordinal))
        {
            state.LogsBindingLost = true;
            state.Owner.MarkSuspended();
            return false;
        }
        return !state.LogsBindingLost;
    }

    private void TryRestoreConfirmedSources(bool verificationDue)
    {
        lock (_lifecycleSync)
        {
            if (_hasPendingDiscoverySnapshot || _stopRequested != 0)
                return;
            if (_confirmedTransferCandidates is null)
            {
                if (verificationDue)
                    foreach (var state in _states.Values.Where(state => state.LogsBindingLost
                        && state.TerminalFault is null && !IsDiscoveryConflicted(state.CanonicalPath)))
                        TryRestoreLogsBinding(state);
                return;
            }
            foreach (var state in _states.Values)
            {
                var owners = _confirmedTransferCandidates.Where(candidate =>
                    WurmDiscoveryPathPolicy.Comparer.Equals(candidate.LogsDirectoryPath, Path.GetDirectoryName(state.CanonicalPath))).ToArray();
                if (owners.Length != 1)
                    continue;
                var candidate = owners[0];
                if (!WurmDiscoveryPathPolicy.Comparer.Equals(candidate.ProfileDirectoryPath, state.Owner.ProfileDirectoryPath))
                {
                    _conflictDirectories.Add(Path.GetDirectoryName(state.CanonicalPath)!);
                    _resumedConflictFiles.Remove(state.CanonicalPath);
                    state.LogsBindingLost = true;
                    state.Owner.MarkRestartRequired();
                    continue;
                }
                if (state.LogsBindingLost && state.TerminalFault is null && !IsDiscoveryConflicted(state.CanonicalPath)
                    && string.Equals(candidate.PhysicalDirectoryKey, state.Owner.PhysicalIdentity, StringComparison.Ordinal)
                    && string.Equals(candidate.PhysicalLogsDirectoryKey, state.PhysicalLogsDirectoryKey, StringComparison.Ordinal)
                    && WurmDiscoveryPathPolicy.Comparer.Equals(candidate.LogicalLogsDirectoryPath, state.LogicalLogsDirectoryPath))
                {
                    TryRestoreLogsBinding(state);
                }
            }
        }
    }

    private void TryRestoreLogsBinding(EventFileState state)
    {
        var ownership = state.Owner.TryOpenRead(out var readGuard);
        using (readGuard)
        {
            if (ownership != ProfileSourceOwnershipStatus.Verified)
                return;
            state.LogsBindingLost = false;
            if (VerifyLogsBinding(state) && !state.BaselinePending && state.TerminalFault is null)
                state.Owner.MarkActivated();
        }
    }

    private IReadOnlyList<EventLogFileCandidate> ScanSourceCandidates()
    {
        var candidates = new List<EventLogFileCandidate>();
        var sourceCandidates = Volatile.Read(ref _sourceCandidates) ?? [];
        foreach (var source in sourceCandidates.Where(source => source.IsEligibleForFutureActivation))
        {
            if (IsControlledSuppressed(source.PhysicalDirectoryKey))
                continue;
            var confirmed = _confirmedTransferCandidates?.Where(candidate =>
                WurmDiscoveryPathPolicy.Comparer.Equals(candidate.LogsDirectoryPath, source.LogsDirectoryPath)).ToArray();
            if (confirmed is { Length: 1 }
                && !WurmDiscoveryPathPolicy.Comparer.Equals(confirmed[0].ProfileDirectoryPath, source.ProfileDirectoryPath))
                continue;
            try
            {
                candidates.AddRange(_sourceCandidateScanner!.ScanKnownProfile(
                    source.DisplayName, source.ProfileDirectoryPath, source.LogsDirectoryPath, allowLinkedLogs: true));
            }
            catch (IOException)
            {
                RecordReadFault();
            }
            catch (UnauthorizedAccessException)
            {
                RecordReadFault();
            }
        }

        return candidates
            .GroupBy(candidate => candidate.EventFilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.EventFilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool IsControlledSuppressed(string? physicalDirectoryKey)
    {
        if (physicalDirectoryKey is null)
            return false;
        lock (_lifecycleSync)
            return IsControlledSuppressedUnsafe(physicalDirectoryKey);
    }

    private bool IsControlledSuppressedUnsafe(string? physicalDirectoryKey) => physicalDirectoryKey is not null
        && _controlledRetirements.ContainsKey(physicalDirectoryKey);

    private bool HasOneCurrentCandidate(string physicalDirectoryKey)
    {
        var candidates = Volatile.Read(ref _sourceCandidates);
        return candidates is not null && candidates.Count(candidate => candidate.IsCurrent
            && string.Equals(candidate.PhysicalDirectoryKey, physicalDirectoryKey, StringComparison.Ordinal)) == 1;
    }

    private void ReleaseProfileNaturally(string? physicalDirectoryKey, EventPipelineSourceStatus source)
    {
        if (_releaseProfileWithIdentity is null)
            return;

        _releaseProfileWithIdentity(source.ProfileId, source.ProfileName);
        if (physicalDirectoryKey is null || _releaseRetiredProfileAsync is null)
            return;

        Task release;
        try { release = _releaseRetiredProfileAsync(source.ProfileId, source.ProfileName); }
        catch (Exception exception) { release = Task.FromException(exception); }

        lock (_lifecycleSync)
            _naturalProfileReleases[physicalDirectoryKey] = new NaturalProfileRelease(physicalDirectoryKey,
                source, release);
    }

    private void RecordControlledRetirementFailure(ControlledRetirement retirement, Exception exception)
    {
        if (retirement.FailureReported)
            return;

        retirement.FailureReported = true;
        if (retirement.ReleaseProfileId is { } profileId && WasDispatcherTerminalFaultReported(profileId, exception))
            return;
        if (retirement.Source is not { } source)
            return;
        var fault = new EventPipelineSourceFault(source.ProfileId, source.ProfileName, source.CanonicalPath,
            source.SourceGeneration, exception);
        lock (_terminalSourceFaultSync)
        {
            _retainedTerminalSourceFaultHistory.Add(fault);
            Volatile.Write(ref _terminalSourceFaults, _retainedTerminalSourceFaultHistory.Snapshot().ToArray());
        }
        RecordReadFault();
        Notify(TerminalSourceFaulted, fault);
    }

    private static Exception GetRetirementReleaseFailure(Task releaseTask) => releaseTask.IsCanceled
        ? new TaskCanceledException("Controlled Event source retirement was canceled.")
        : (Exception?)releaseTask.Exception ?? new InvalidOperationException("Controlled Event source retirement failed.");

    private void AttachDispatcherTerminalFaults(FastEventDispatcher dispatcher)
    {
        _dispatcherTerminalFaultHandler = status =>
        {
            lock (_dispatcherTerminalFaultSync)
            {
                if (!ReferenceEquals(_dispatcherForTerminalFaults, dispatcher))
                    return;
                _dispatcherTerminalFaults.Add(status);
            }
        };
        lock (_dispatcherTerminalFaultSync)
            _dispatcherForTerminalFaults = dispatcher;
        dispatcher.TerminalStatusRecorded += _dispatcherTerminalFaultHandler;
    }

    private void DetachDispatcherTerminalFaults()
    {
        lock (_dispatcherTerminalFaultSync)
        {
            if (_dispatcherForTerminalFaults is not null && _dispatcherTerminalFaultHandler is not null)
                _dispatcherForTerminalFaults.TerminalStatusRecorded -= _dispatcherTerminalFaultHandler;
            _dispatcherForTerminalFaults = null;
            _dispatcherTerminalFaultHandler = null;
            _dispatcherTerminalFaults.Clear();
        }
    }

    private bool WasDispatcherTerminalFaultReported(PlayerProfileId profileId, Exception failure)
    {
        lock (_dispatcherTerminalFaultSync)
            return _dispatcherTerminalFaults.TryConsume(profileId, failure);
    }

    private static IEnumerable<Exception> FlattenFailures(Exception failure) => failure is AggregateException aggregate
        ? aggregate.Flatten().InnerExceptions
        : [failure];

    private void TryRebindOwner(EventFileState state)
    {
        if (state.LogsBindingLost || !VerifyLogsBinding(state))
            return;
        state.OwnerRebindPending = true;
        using var admission = TryBeginOutputAdmission();
        if (admission is null)
            return;
        if (!_profileIdentityResolver.TryRegisterSource(state.Owner.ProfileDirectoryPath, out var replacement, out var readGuard)
            || replacement is null)
        {
            RecordReadFault();
            return;
        }
        var transferred = false;
        using (readGuard)
        {
            try
            {
                var oldId = state.ProfileId;
                var oldPhysicalDirectoryKey = state.Owner.PhysicalIdentity;
                var generation = checked(state.Generation + 1);
                if (Handoff(new EventContinuityLoss(oldId, state.ProfileName, state.CanonicalPath,
                    generation, StreamGapReason.Replaced)) != EventPipelineHandoffResult.Complete)
                    return;

                state.Owner.Dispose();
                state.Owner = replacement;
                _pendingOwnerDiscoveryBaselines.Add(replacement.Identity.Id);
                transferred = true;
                state.OwnerRebindPending = false;
                state.Generation = generation;
                state.BaselinePending = true;
                state.PendingText = string.Empty;
                state.DisappearancePending = false;
                state.VerificationPending = false;
                state.Missing = false;
                Notify(SourceDiscovered, new EventPipelineSourceStatus(
                    state.ProfileId, state.ProfileName, state.CanonicalPath, state.Generation));
                TryBaselineCore(state);
                Volatile.Write(ref _metrics, _metrics with { ContinuityEvents = _metrics.ContinuityEvents + 1 });
                if (!_states.Values.Any(candidate => candidate.ProfileId == oldId))
                    ReleaseProfileNaturally(oldPhysicalDirectoryKey, new EventPipelineSourceStatus(oldId,
                        state.ProfileName, state.CanonicalPath, generation - 1));
            }
            finally
            {
                if (!transferred)
                    replacement.Dispose();
            }
        }
    }

    private sealed class OutputAdmission(EventPipeline owner) : IDisposable
    {
        private EventPipeline? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndOutputAdmission();
    }

    private sealed class ControlledRetirement(string physicalDirectoryKey, long generation, long snapshotVersion)
    {
        internal const int Requested = 0;
        internal const int Releasing = 1;
        internal const int Completed = 2;
        internal const int Failed = 3;
        public string PhysicalDirectoryKey { get; } = physicalDirectoryKey;
        public long Generation { get; set; } = generation;
        public long SnapshotVersion { get; set; } = snapshotVersion;
        public PlayerProfileId? ReleaseProfileId { get; set; }
        public Task? ReleaseTask { get; set; }
        public EventPipelineSourceStatus? Source { get; set; }
        public bool FailureReported { get; set; }
        public long? PendingReappearanceGeneration { get; set; }
        public long PendingReappearanceSnapshotVersion { get; set; }
        public int Phase;
    }

    private sealed record NaturalProfileRelease(string? PhysicalDirectoryKey, EventPipelineSourceStatus Source,
        Task? ReleaseTask)
    {
        internal PlayerProfileId ProfileId => Source.ProfileId;
    }
}

/// <summary>
/// A handoff may have transferred responsibility for an output, but could not
/// establish its final transfer state. Replaying would risk duplication.
/// </summary>
public sealed class EventPipelineHandoffIndeterminateException : Exception
{
    internal EventPipelineHandoffIndeterminateException(
        EventPipelineOutput output,
        Exception? cause = null)
        : base("Event Pipeline handoff outcome is indeterminate; source reconciliation is required.", cause)
    {
        Output = output;
    }

    public EventPipelineOutput Output { get; }
}
