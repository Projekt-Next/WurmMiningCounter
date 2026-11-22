using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.MiningStatistics;

/// <summary>
/// Application-lifetime, per-profile observation of the client-declared _Skills writer.
/// It owns source discovery and live state, while profile identity and UI remain outside it.
/// </summary>
public sealed class LiveSkillMonitor : IDisposable
{
    internal static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromMilliseconds(250);
    internal const int MaximumConsoleRecordLength = 64 * 1024;

    private static readonly Regex SkillsFileNamePattern = new(
        @"^_Skills\.(?<year>\d{4})-(?<month>\d{2})(?:-(?<day>\d{2}))?\.txt\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex WriterDeclarationPattern = new(
        @"^\[(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d\] Writing to (?<path>.+)\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly AsyncLocal<LiveSkillMonitor?> CurrentDispatchMonitor = new();
    private static readonly AsyncLocal<LiveSkillMonitor?> CurrentWorkerMonitor = new();

    private readonly string _playersDirectoryPath;
    private readonly TimeSpan _pollingInterval;
    private readonly RuntimeDiagnosticService? _diagnostics;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, MiningSkillsSourceReader> _sourceReaderFactory;
    private readonly Func<string, Stream> _consoleStreamFactory;
    private readonly Func<string, IEnumerable<string>> _enumerateSkillFiles;
    // Immutable candidate arrays are atomically replaced only by explicit,
    // add-only discovery refresh.  The regular poll never performs root scan.
    private LogDiscoverySourceCandidate[]? _sourceCandidates;
    private long _sourceCandidateSnapshotVersion;
    private readonly object _sync = new();
    private readonly object _notificationSync = new();
    private readonly Dictionary<PlayerProfileId, ProfileState> _profiles = [];
    private readonly Dictionary<string, PendingProfileRegistration> _pendingProfileRegistrations = new(WurmDiscoveryPathPolicy.Comparer);
    private readonly HashSet<string> _resolvedProfileDirectories = new(WurmDiscoveryPathPolicy.Comparer);
    // Versioned controlled-retirement suppressions, driven by App's serialized
    // complete Discovery reconciliation.
    private readonly Dictionary<string, SkillsSuppression> _suppressions = new(StringComparer.Ordinal);
    private readonly PlayerProfileIdentityResolver _profileIdentityResolver;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private int _activeNotificationDispatches;
    private int _resourcesDisposed;
    private bool _notificationsClosed;
    private bool _started;
    private bool _disposed;

    public LiveSkillMonitor(TimeSpan? pollingInterval = null, RuntimeDiagnosticService? diagnostics = null)
        : this(LocatePlayersDirectory(), pollingInterval ?? DefaultPollingInterval, diagnostics, startWorker: true)
    {
    }

    internal LiveSkillMonitor(
        string playersDirectoryPath,
        TimeSpan pollingInterval,
        RuntimeDiagnosticService? diagnostics = null,
        bool startWorker = false,
        Func<string, MiningSkillsSourceReader>? sourceReaderFactory = null,
        Func<string, Stream>? consoleStreamFactory = null,
        TimeProvider? timeProvider = null,
        PlayerProfileIdentityResolver? profileIdentityResolver = null,
        Func<string, IEnumerable<string>>? enumerateSkillFiles = null,
        IReadOnlyList<LogDiscoverySourceCandidate>? sourceCandidates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playersDirectoryPath);
        if (pollingInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollingInterval));

        _playersDirectoryPath = NormalizePath(playersDirectoryPath);
        _pollingInterval = pollingInterval;
        _diagnostics = diagnostics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sourceReaderFactory = sourceReaderFactory ?? (path => new MiningSkillsSourceReader(path));
        _consoleStreamFactory = consoleStreamFactory ?? OpenConsoleStream;
        _enumerateSkillFiles = enumerateSkillFiles ?? (path => Directory.EnumerateFiles(path, "_Skills*.txt", SearchOption.TopDirectoryOnly));
        _sourceCandidates = sourceCandidates?.Where(candidate => candidate.IsCurrent).ToArray();
        _profileIdentityResolver = profileIdentityResolver ?? new PlayerProfileIdentityResolver();
        StartWorker = startWorker;
    }

    private bool StartWorker { get; }

    public event EventHandler<LiveSkillProfileChangedEventArgs>? ProfileChanged;

    /// <summary>
    /// Adds current candidates from a newer shared discovery snapshot.  It
    /// intentionally never removes a candidate or releases a live owner.
    /// </summary>
    internal bool AddSourceCandidates(long snapshotVersion, IReadOnlyList<LogDiscoverySourceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_sync)
        {
            if (_disposed || _sourceCandidates is null || snapshotVersion <= _sourceCandidateSnapshotVersion)
                return false;

            var known = new HashSet<string>(_sourceCandidates.Select(candidate => candidate.ProfileDirectoryPath),
                WurmDiscoveryPathPolicy.Comparer);
            var additions = candidates.Where(candidate => candidate.IsCurrent
                    && known.Add(candidate.ProfileDirectoryPath))
                .ToArray();
            _sourceCandidateSnapshotVersion = snapshotVersion;
            if (additions.Length == 0)
                return false;

            _sourceCandidates = _sourceCandidates.Concat(additions).ToArray();
            return true;
        }
    }

    /// <summary>
    /// Retires exactly one confirmed Skills ownership registration. This is not
    /// wired to Discovery publication until the suppression executor exists.
    /// </summary>
    internal bool TryRetireSource(string physicalDirectoryKey, long generation, long snapshotVersion)
    {
        if (string.IsNullOrWhiteSpace(physicalDirectoryKey) || generation <= 0 || snapshotVersion <= 0)
            return false;

        LiveSkillProfileChangedEventArgs? notification = null;
        lock (_sync)
        {
            if (_disposed)
                return false;
            if (_suppressions.TryGetValue(physicalDirectoryKey, out var existing))
                return existing.Generation == generation && existing.SnapshotVersion == snapshotVersion;
            if (!HasOneCurrentCandidate(physicalDirectoryKey))
                return false;

            var active = _profiles.Values.Where(profile => !profile.Retired
                    && string.Equals(profile.Owner.PhysicalIdentity, physicalDirectoryKey, StringComparison.Ordinal))
                .ToArray();
            var pending = _pendingProfileRegistrations.Where(pair => string.Equals(pair.Value.Owner.PhysicalIdentity,
                    physicalDirectoryKey, StringComparison.Ordinal)).ToArray();
            if (active.Length + pending.Length != 1)
                return false;

            _suppressions.Add(physicalDirectoryKey, new SkillsSuppression(generation, snapshotVersion));
            if (pending.Length == 1)
            {
                _pendingProfileRegistrations.Remove(pending[0].Key);
                _resolvedProfileDirectories.Remove(pending[0].Key);
                pending[0].Value.Owner.Dispose();
            }
            else
            {
                var profile = active[0];
                _resolvedProfileDirectories.Remove(profile.Owner.ProfileDirectoryPath);
                RetireProfileOwner(profile, []);
                notification = new LiveSkillProfileChangedEventArgs(profile.Id, CreateSnapshot(profile));
            }
        }

        if (notification is not null)
            DispatchNotifications([notification]);
        return true;
    }

    /// <summary>
    /// Removes one current suppression only when it is superseded by a newer
    /// generation/version and exactly one current candidate proves the key.
    /// </summary>
    internal bool TryPermitReappearance(string physicalDirectoryKey, long retiredGeneration,
        long reappearanceGeneration, long snapshotVersion)
    {
        if (string.IsNullOrWhiteSpace(physicalDirectoryKey) || reappearanceGeneration <= retiredGeneration)
            return false;

        lock (_sync)
        {
            if (_disposed || !_suppressions.TryGetValue(physicalDirectoryKey, out var suppression)
                || suppression.Generation != retiredGeneration || snapshotVersion <= suppression.SnapshotVersion
                || !HasOneCurrentCandidate(physicalDirectoryKey))
                return false;

            _suppressions.Remove(physicalDirectoryKey);
            return true;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_started)
                return;

            DiscoverProfiles();
            _started = true;
            if (StartWorker)
                _worker = Task.Run(PollLoopAsync);
        }
    }

    public IReadOnlyList<LiveSkillProfileSnapshot> GetSnapshots()
    {
        lock (_sync)
        {
            return _profiles.Values
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(profile => new LiveSkillProfileSnapshot(
                    profile.Id,
                    profile.Name,
                    profile.ActiveSourcePath,
                    profile.SourceState,
                    profile.Tracker.GetSnapshot())
                {
                    ObservationStartedAtUtc = profile.ObservationStartedAtUtc,
                    PresentationRootPath = profile.PresentationRootPath,
                    PresentationGroupName = profile.PresentationGroupName
                })
                .ToArray();
        }
    }

    internal void Poll()
    {
        IReadOnlyList<LiveSkillProfileChangedEventArgs> notifications;
        lock (_sync)
        {
            if (!_started || _disposed)
                return;

            var existingProfiles = _profiles.Values.ToDictionary(profile => profile.Id, profile => profile.Retired);
            DiscoverProfiles();
            var changed = new List<LiveSkillProfileChangedEventArgs>();
            foreach (var profile in _profiles.Values.Where(profile => !profile.Retired)
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                // A retained retired state has the same PlayerProfileId but
                // represents no active source. Its rebind must publish even
                // before the new writer produces another skill update.
                var isNewProfile = !existingProfiles.TryGetValue(profile.Id, out var wasRetired) || wasRetired;
                var before = isNewProfile ? null : CreateSnapshot(profile);
                var effectiveSourceState = profile.SuspendedSourceState ?? profile.SourceState;
                var needsSourceRead = File.Exists(profile.ConsolePath)
                    || (profile.ActiveSourcePath is not null
                        && effectiveSourceState is LiveSkillSourceState.Observing or LiveSkillSourceState.WaitingForSource);
                if (needsSourceRead)
                {
                    var ownership = profile.Owner.TryOpenRead(out var readGuard);
                    using (readGuard)
                    {
                        if (ownership == ProfileSourceOwnershipStatus.Changed)
                        {
                            RebindProfileOwner(profile, changed);
                            continue;
                        }
                        if (ownership == ProfileSourceOwnershipStatus.Unavailable)
                            SuspendOwnership(profile);
                        else
                        {
                            if (profile.SuspendedSourceState is { } sourceState)
                            {
                                profile.SourceState = sourceState;
                                profile.SuspendedSourceState = null;
                            }
                            ProcessConsole(profile);
                            ProcessActiveSource(profile);
                            profile.Owner.MarkActivated();
                        }
                    }
                }
                var after = CreateSnapshot(profile);
                if (isNewProfile || !HaveSameSnapshot(before!, after))
                    changed.Add(new LiveSkillProfileChangedEventArgs(profile.Id, after));
            }

            notifications = changed;
        }

        DispatchNotifications(notifications);
    }

    public void Dispose()
    {
        Task? worker;
        CloseNotificationAdmission();
        lock (_sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                _shutdown.Cancel();
            }

            worker = _worker;
        }

        if (ReferenceEquals(CurrentDispatchMonitor.Value, this)
            || ReferenceEquals(CurrentWorkerMonitor.Value, this))
        {
            return;
        }

        try { worker?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }

        lock (_notificationSync)
        {
            while (_activeNotificationDispatches != 0)
                Monitor.Wait(_notificationSync);
        }

        CompleteDisposal();
    }

    private async Task PollLoopAsync()
    {
        var previousWorkerMonitor = CurrentWorkerMonitor.Value;
        CurrentWorkerMonitor.Value = this;
        try
        {
            using var timer = new PeriodicTimer(_pollingInterval);
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                ExecuteWorkerPoll();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            CurrentWorkerMonitor.Value = previousWorkerMonitor;
            var disposed = false;
            lock (_sync)
            {
                disposed = _disposed;
            }

            if (disposed)
                CompleteDisposal();
        }
    }

    internal void ExecuteWorkerPollForTest() => ExecuteWorkerPoll();

    internal (int PendingByteCount, bool IsDiscardingOversizedRecord) GetConsoleTailStateForTest(PlayerProfileId profileId)
    {
        lock (_sync)
        {
            var tail = _profiles[profileId].ConsoleTail;
            return (tail.PendingByteCount, tail.IsDiscardingOversizedRecord);
        }
    }

    private void ExecuteWorkerPoll()
    {
        var previousWorkerMonitor = CurrentWorkerMonitor.Value;
        CurrentWorkerMonitor.Value = this;
        try
        {
            try { Poll(); }
            catch (Exception exception)
            {
                _diagnostics?.Report(RuntimeDiagnosticLevel.Warning, nameof(LiveSkillMonitor), "Live skill poll failed: {0}", exception.GetType().Name);
            }
        }
        finally
        {
            CurrentWorkerMonitor.Value = previousWorkerMonitor;
        }
    }

    private void DispatchNotifications(IReadOnlyList<LiveSkillProfileChangedEventArgs> notifications)
    {
        if (notifications.Count == 0)
            return;

        lock (_notificationSync)
        {
            if (_notificationsClosed)
                return;

            _activeNotificationDispatches++;
        }

        try
        {
            var previousDispatchMonitor = CurrentDispatchMonitor.Value;
            CurrentDispatchMonitor.Value = this;
            try
            {
            foreach (var notification in notifications)
            {
                var handlers = ProfileChanged;
                if (handlers is null)
                    continue;

                foreach (EventHandler<LiveSkillProfileChangedEventArgs> handler in handlers.GetInvocationList())
                {
                    if (NotificationsAreClosed())
                        return;

                    try
                    {
                        handler(this, notification);
                    }
                    catch (Exception exception)
                    {
                        _diagnostics?.Report(RuntimeDiagnosticLevel.Warning, nameof(LiveSkillMonitor), "Live skill profile subscriber failed: {0}", exception.GetType().Name);
                    }
                }
            }
            }
            finally
            {
                CurrentDispatchMonitor.Value = previousDispatchMonitor;
            }
        }
        finally
        {
            lock (_notificationSync)
            {
                _activeNotificationDispatches--;
                Monitor.PulseAll(_notificationSync);
            }

            CompleteDisposalWithoutWorker();
        }
    }

    private void CloseNotificationAdmission()
    {
        lock (_notificationSync)
            _notificationsClosed = true;
    }

    private bool NotificationsAreClosed()
    {
        lock (_notificationSync)
            return _notificationsClosed;
    }

    private void CompleteDisposalWithoutWorker()
    {
        lock (_sync)
        {
            if (!_disposed || _worker is not null)
                return;
        }

        CompleteDisposal();
    }

    private void CompleteDisposal()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            return;

        lock (_sync)
        {
            foreach (var profile in _profiles.Values)
                profile.Owner.Dispose();
            foreach (var pending in _pendingProfileRegistrations.Values)
                pending.Owner.Dispose();
        }
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private void DiscoverProfiles()
    {
        if (_sourceCandidates is not null)
        {
            DiscoverSourceCandidates();
            return;
        }

        if (!Directory.Exists(_playersDirectoryPath))
            return;

        foreach (var profileDirectory in Directory.EnumerateDirectories(_playersDirectoryPath))
        {
            var profileName = Path.GetFileName(profileDirectory);
            var normalizedProfileDirectory = NormalizePath(profileDirectory);
            if (string.IsNullOrWhiteSpace(profileName))
                continue;

            if (_pendingProfileRegistrations.TryGetValue(normalizedProfileDirectory, out var pending))
            {
                TryActivatePendingRegistration(normalizedProfileDirectory, pending);
                continue;
            }

            if (!_resolvedProfileDirectories.Add(normalizedProfileDirectory))
                continue;

            var logsDirectoryPath = Path.Combine(profileDirectory, "logs");
            if (!Directory.Exists(logsDirectoryPath))
            {
                _resolvedProfileDirectories.Remove(normalizedProfileDirectory);
                continue;
            }

            if (!_profileIdentityResolver.TryRegisterSource(normalizedProfileDirectory, out var owner, out var readGuard)
                || owner is null)
                continue;
            var registeredOwner = owner;
            var transferred = false;
            using (readGuard)
            {
                try
                {
                    if (_profiles.ContainsKey(registeredOwner.Identity.Id))
                        continue;
                    var normalizedLogsDirectory = NormalizePath(logsDirectoryPath);
                    var registration = new PendingProfileRegistration(registeredOwner, profileName, normalizedLogsDirectory,
                        Path.Combine(Path.GetDirectoryName(_playersDirectoryPath)!, $"console.{profileName}.log"));
                    if (TryCreateProfile(registration, out var profile) && profile is not null)
                    {
                        _profiles.Add(profile.Id, profile);
                        transferred = true;
                    }
                    else if (registeredOwner.PhysicalIdentity is not null)
                    {
                        _pendingProfileRegistrations.Add(normalizedProfileDirectory, registration);
                        transferred = true;
                    }
                }
                finally
                {
                    if (!transferred)
                        registeredOwner.Dispose();
                }
            }
        }
    }

    private void DiscoverSourceCandidates()
    {
        foreach (var candidate in _sourceCandidates!.Where(candidate => candidate.IsEligibleForFutureActivation))
        {
            string profileDirectoryPath;
            string logsDirectoryPath;
            string consolePath;
            try
            {
                profileDirectoryPath = NormalizePath(candidate.ProfileDirectoryPath);
                logsDirectoryPath = NormalizePath(candidate.LogsDirectoryPath);
                consolePath = NormalizePath(candidate.ConsolePath);
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (NotSupportedException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(candidate.DisplayName)
                || !WurmDiscoveryPathPolicy.Comparer.Equals(Path.GetDirectoryName(logsDirectoryPath) ?? string.Empty,
                    profileDirectoryPath))
            {
                continue;
            }

            if (IsSuppressed(candidate.PhysicalDirectoryKey))
                continue;

            if (_pendingProfileRegistrations.TryGetValue(profileDirectoryPath, out var pending))
            {
                if (IsSuppressed(pending.Owner.PhysicalIdentity))
                {
                    _pendingProfileRegistrations.Remove(profileDirectoryPath);
                    _resolvedProfileDirectories.Remove(profileDirectoryPath);
                    pending.Owner.Dispose();
                    continue;
                }
                TryActivatePendingRegistration(profileDirectoryPath, pending);
                continue;
            }

            if (!_resolvedProfileDirectories.Add(profileDirectoryPath))
                continue;

            if (!_profileIdentityResolver.TryRegisterSource(profileDirectoryPath, out var owner, out var readGuard)
                || owner is null)
                continue;

            var transferred = false;
            using (readGuard)
            {
                try
                {
                    if (candidate.PhysicalDirectoryKey is not null
                        && !string.Equals(candidate.PhysicalDirectoryKey, owner.PhysicalIdentity, StringComparison.Ordinal))
                        continue;
                    _profiles.TryGetValue(owner.Identity.Id, out var priorState);
                    if (priorState is not null && !priorState.Retired)
                        continue;

                    var registration = new PendingProfileRegistration(owner, candidate.DisplayName, logsDirectoryPath, consolePath,
                        priorState, priorState?.ConsoleTail, PresentationRootPath: Path.GetDirectoryName(profileDirectoryPath),
                        PresentationGroupName: GetPresentationGroupName(candidate.Origins));
                    if (TryCreateProfile(registration, out var profile) && profile is not null)
                    {
                        _profiles[profile.Id] = profile;
                        transferred = true;
                    }
                    else if (owner.PhysicalIdentity is not null)
                    {
                        _pendingProfileRegistrations[profileDirectoryPath] = registration;
                        transferred = true;
                    }
                }
                finally
                {
                    if (!transferred)
                        owner.Dispose();
                }
            }
        }
    }

    private void TryActivatePendingRegistration(string profileDirectoryPath, PendingProfileRegistration pending)
    {
        var ownership = pending.Owner.TryOpenRead(out var readGuard);
        using (readGuard)
        {
            if (ownership == ProfileSourceOwnershipStatus.Changed)
            {
                _pendingProfileRegistrations.Remove(profileDirectoryPath);
                pending.Owner.Dispose();
                if (pending.ConsoleTail is null)
                    _resolvedProfileDirectories.Remove(profileDirectoryPath);
                else if (_profileIdentityResolver.TryRegisterSource(profileDirectoryPath, out var owner, out var replacementReadGuard)
                    && owner is not null)
                {
                    using (replacementReadGuard)
                    {
                        if (owner.PhysicalIdentity is not null)
                        {
                            _profiles.TryGetValue(owner.Identity.Id, out var priorState);
                            _pendingProfileRegistrations.Add(profileDirectoryPath,
                            new PendingProfileRegistration(owner, pending.ProfileName, pending.LogsDirectoryPath, pending.ConsolePath,
                                    priorState, pending.ConsoleTail, pending.PresentationRootPath, pending.PresentationGroupName));
                        }
                        else
                            owner.Dispose();
                    }
                }
                return;
            }
            if (ownership != ProfileSourceOwnershipStatus.Verified)
                return;

            // An alias may have activated the same confirmed identity while this
            // path was pending. Do not create a second tracker or lose its owner.
            if (_profiles.TryGetValue(pending.Owner.Identity.Id, out var existing) && !existing.Retired)
            {
                _pendingProfileRegistrations.Remove(profileDirectoryPath);
                pending.Owner.Dispose();
                return;
            }
            if (!TryCreateProfile(pending, out var profile) || profile is null)
                return;

            _pendingProfileRegistrations.Remove(profileDirectoryPath);
            _profiles[profile.Id] = profile;
        }
    }

    private bool TryCreateProfile(PendingProfileRegistration pending, out ProfileState? profile)
    {
        try
        {
            profile = CreateProfile(pending.Owner, pending.ProfileName, pending.LogsDirectoryPath, pending.ConsolePath,
                pending.PriorState, pending.ConsoleTail, pending.PresentationRootPath, pending.PresentationGroupName);
            pending.Owner.MarkActivated();
            pending.FailureKind = null;
            return true;
        }
        catch (IOException exception)
        {
            ReportInitializationFailure(pending, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportInitializationFailure(pending, exception);
        }

        profile = null;
        return false;
    }

    private void ReportInitializationFailure(PendingProfileRegistration pending, Exception exception)
    {
        var failureKind = exception.GetType();
        if (pending.FailureKind == failureKind)
            return;

        pending.FailureKind = failureKind;
        _diagnostics?.Report(RuntimeDiagnosticLevel.Warning, nameof(LiveSkillMonitor),
            "Live skill profile initialization failed: {0}", failureKind.Name);
    }

    private ProfileState CreateProfile(ProfileSourceOwner owner, string name, string logsDirectoryPath, string consolePath,
        ProfileState? priorState = null, ConsoleTail? consoleTail = null, string? presentationRootPath = null, string? presentationGroupName = null)
    {
        var profile = new ProfileState(
            owner, name, logsDirectoryPath,
            consolePath,
            _consoleStreamFactory, priorState?.ObservationStartedAtUtc ?? _timeProvider.GetUtcNow(),
            priorState?.Tracker, consoleTail, presentationRootPath ?? priorState?.PresentationRootPath,
            presentationGroupName ?? priorState?.PresentationGroupName ?? "Unknown installation");
        profile.BootstrapCompleted = priorState?.BootstrapCompleted ?? false;
        profile.RequireFreshWriterDeclaration = priorState?.Retired == true;
        CaptureStartupSources(profile);
        ProcessConsole(profile);
        BootstrapMiningCurrent(profile);
        profile.IsInitialized = true;
        return profile;
    }

    private static void SuspendOwnership(ProfileState profile)
    {
        profile.SuspendedSourceState ??= profile.SourceState;
        profile.SourceState = LiveSkillSourceState.Incomplete;
    }

    private static string GetPresentationGroupName(IReadOnlyList<WurmLogLocationOrigin> origins)
    {
        if (origins.Any(origin => origin.IsManual)) return "Additional folders";
        if (origins.Any(origin => origin.RuleId == "wurm-online-standard")) return "Wurm Online Standard";
        if (origins.Any(origin => origin.RuleId == "wurm-online-steam")) return "Wurm Online Steam";
        if (origins.Any(origin => origin.RuleId == "wurm-unlimited-steam")) return "Wurm Unlimited Steam";
        return "Unknown installation";
    }

    private void RebindProfileOwner(ProfileState profile, List<LiveSkillProfileChangedEventArgs> changed)
    {
        if (!_profileIdentityResolver.TryRegisterSource(profile.Owner.ProfileDirectoryPath, out var owner, out var readGuard)
            || owner is null)
        {
            RetireProfileOwner(profile, changed);
            return;
        }
        var transferred = false;
        using (readGuard)
        {
            try
            {
                ProfileState? replacement = null;
                if (!_profiles.TryGetValue(owner.Identity.Id, out var existing) || existing.Retired
                    || ReferenceEquals(existing, profile))
                {
                    // A declaration already consumed for the previous physical
                    // owner cannot select the replacement's writer. Keep the
                    // console checkpoint, but drop any unfinished old record.
                    profile.ConsoleTail.DiscardPendingRecord();
                    var pending = new PendingProfileRegistration(owner, profile.Name, profile.LogsDirectoryPath, profile.ConsolePath,
                        existing, profile.ConsoleTail, profile.PresentationRootPath, profile.PresentationGroupName);
                    if (!TryCreateProfile(pending, out replacement) && owner.PhysicalIdentity is not null)
                    {
                        _pendingProfileRegistrations.Add(owner.ProfileDirectoryPath, pending);
                        transferred = true;
                    }
                }

                RetireProfileOwner(profile, changed);
                if (replacement is not null)
                {
                    _profiles[replacement.Id] = replacement;
                    transferred = true;
                    changed.Add(new LiveSkillProfileChangedEventArgs(replacement.Id, CreateSnapshot(replacement)));
                }
            }
            catch
            {
                // Initialization must not become resolution on every poll.
                RetireProfileOwner(profile, changed);
                throw;
            }
            finally
            {
                if (!transferred)
                    owner.Dispose();
            }
        }
    }

    private static void RetireProfileOwner(ProfileState profile, List<LiveSkillProfileChangedEventArgs> changed)
    {
        profile.Retired = true;
        profile.SourceState = LiveSkillSourceState.Incomplete;
        profile.ActiveSourcePath = null;
        profile.Owner.Dispose();
        changed.Add(new LiveSkillProfileChangedEventArgs(profile.Id, CreateSnapshot(profile)));
    }

    private void CaptureStartupSources(ProfileState profile)
    {
        foreach (var sourcePath in _enumerateSkillFiles(profile.LogsDirectoryPath)
                     .Where(path => IsValidSourcePath(profile, path, out _))
                     .Select(NormalizePath)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            profile.Sources.Add(sourcePath, new SourceRegistration(null, null, IsStartupSource: true, CaptureFailed: false));
            try
            {
                var reader = _sourceReaderFactory(sourcePath);
                var boundary = reader.CaptureSessionBoundary();
                profile.Sources[sourcePath] = new SourceRegistration(reader, boundary, IsStartupSource: true, CaptureFailed: false);
            }
            catch (IOException)
            {
                profile.Sources[sourcePath] = new SourceRegistration(null, null, IsStartupSource: true, CaptureFailed: true);
                _diagnostics?.Report(RuntimeDiagnosticLevel.Warning, nameof(LiveSkillMonitor), "Could not capture a live skill boundary for '{0}'.", sourcePath);
            }
            catch (UnauthorizedAccessException)
            {
                profile.Sources[sourcePath] = new SourceRegistration(null, null, IsStartupSource: true, CaptureFailed: true);
                _diagnostics?.Report(RuntimeDiagnosticLevel.Warning, nameof(LiveSkillMonitor), "Live skill boundary access was denied for '{0}'.", sourcePath);
            }
        }
    }

    private void ProcessConsole(ProfileState profile)
    {
        if (!File.Exists(profile.ConsolePath))
            return;

        try
        {
            var result = profile.ConsoleTail.Read(profile.ConsolePath);
            profile.ConsoleFailureKind = null;
            if (result.Reestablished)
            {
                if (profile.RequireFreshWriterDeclaration)
                {
                    ResetConsoleWriter(profile);
                    profile.RequireFreshWriterDeclaration = false;
                    return;
                }
                if (TryGetLastValidWriterDeclaration(profile, result.CompleteLines, out var lastWriterDeclaration))
                    TryApplyWriterDeclaration(profile, lastWriterDeclaration);
                else
                    ResetConsoleWriter(profile);
            }
            else
            {
                foreach (var line in result.CompleteLines)
                    TryApplyWriterDeclaration(profile, line);
            }

            if (result.OversizedRecordStarted)
            {
                _diagnostics?.Report(RuntimeDiagnosticLevel.Warning, nameof(LiveSkillMonitor),
                    "Ignored an oversized live skill console record for profile '{0}'.", profile.Name);
            }
        }
        catch (IOException exception)
        {
            ReportConsoleFailure(profile, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportConsoleFailure(profile, exception);
        }
    }

    private void ReportConsoleFailure(ProfileState profile, Exception exception)
    {
        var failureKind = exception.GetType();
        if (profile.ConsoleFailureKind == failureKind)
            return;

        profile.ConsoleFailureKind = failureKind;
        var message = exception is UnauthorizedAccessException
            ? "Live skill console access was denied for profile '{0}'."
            : "Could not read the live skill console for profile '{0}'.";
        _diagnostics?.Report(RuntimeDiagnosticLevel.Warning, nameof(LiveSkillMonitor), message, profile.Name);
    }

    private bool TryGetLastValidWriterDeclaration(ProfileState profile, IReadOnlyList<string> lines, out string declaration)
    {
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var match = WriterDeclarationPattern.Match(lines[index]);
            if (match.Success && IsValidSourcePath(profile, match.Groups["path"].Value, out _))
            {
                declaration = lines[index];
                return true;
            }
        }

        declaration = string.Empty;
        return false;
    }

    private static void ResetConsoleWriter(ProfileState profile)
    {
        profile.ActiveSourcePath = null;
        profile.SourceState = LiveSkillSourceState.Unknown;
    }

    private void TryApplyWriterDeclaration(ProfileState profile, string line)
    {
        var match = WriterDeclarationPattern.Match(line);
        if (!match.Success || !IsValidSourcePath(profile, match.Groups["path"].Value, out var sourcePath))
            return;

        if (string.Equals(profile.ActiveSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            return;

        if (profile.IsInitialized && profile.ActiveSourcePath is not null)
            ProcessActiveSource(profile);

        profile.ActiveSourcePath = sourcePath;
        if (!profile.Sources.TryGetValue(sourcePath, out var source))
        {
            if (!File.Exists(sourcePath))
            {
                profile.SourceState = LiveSkillSourceState.WaitingForSource;
                return;
            }

            source = CreatePostStartSource(sourcePath);
            profile.Sources.Add(sourcePath, source);
        }

        if (source.CaptureFailed)
        {
            profile.SourceState = LiveSkillSourceState.Incomplete;
            return;
        }

        profile.SourceState = LiveSkillSourceState.Observing;
    }

    private void BootstrapMiningCurrent(ProfileState profile)
    {
        if (profile.BootstrapCompleted)
            return;

        profile.BootstrapCompleted = true;
        var candidates = new List<KeyValuePair<string, SourceRegistration>>();
        if (profile.ActiveSourcePath is not null
            && profile.Sources.TryGetValue(profile.ActiveSourcePath, out var active)
            && active.IsStartupSource
            && active.Boundary is not null)
        {
            candidates.Add(new(profile.ActiveSourcePath, active));
        }

        candidates.AddRange(profile.Sources
            .Where(pair => pair.Value.IsStartupSource
                && pair.Value.Boundary is not null
                && !string.Equals(pair.Key, profile.ActiveSourcePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => GetSourceSortKey(pair.Key))
            .ThenBy(pair => Path.GetFileName(pair.Key), StringComparer.Ordinal));

        var bootstrapper = new MiningCurrentBootstrapper();
        foreach (var (sourcePath, source) in candidates)
        {
            var result = bootstrapper.Find(sourcePath, source.Boundary!);
            if (result.Status == MiningCurrentBootstrapStatus.Found)
            {
                profile.Tracker.SeedCurrent("Mining", result.CurrentValue!.Value);
                return;
            }

            if (result.Status == MiningCurrentBootstrapStatus.ContinuityLost)
            {
                if (string.Equals(sourcePath, profile.ActiveSourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    profile.SourceState = LiveSkillSourceState.Incomplete;
                    return;
                }

                _diagnostics?.Report(RuntimeDiagnosticLevel.Warning, nameof(LiveSkillMonitor), "Skipped an unreliable Mining bootstrap source for profile '{0}'.", profile.Name);
            }
        }
    }

    private void ProcessActiveSource(ProfileState profile)
    {
        if (profile.SourceState == LiveSkillSourceState.WaitingForSource
            && profile.ActiveSourcePath is not null
            && File.Exists(profile.ActiveSourcePath))
        {
            var createdSource = CreatePostStartSource(profile.ActiveSourcePath);
            profile.Sources[profile.ActiveSourcePath] = createdSource;
            profile.SourceState = LiveSkillSourceState.Observing;
        }

        if (profile.SourceState != LiveSkillSourceState.Observing
            || profile.ActiveSourcePath is null
            || !profile.Sources.TryGetValue(profile.ActiveSourcePath, out var source)
            || source.Reader is null)
        {
            return;
        }

        try
        {
            var result = source.Reader.ReadNewLines();
            if (result.HasContinuityLoss)
            {
                profile.SourceState = LiveSkillSourceState.Incomplete;
                return;
            }

            foreach (var line in result.Lines)
            {
                var update = NumericSkillUpdateParser.Parse(line);
                if (update is not null)
                    profile.Tracker.Apply(update);
            }
        }
        catch (IOException)
        {
            profile.SourceState = LiveSkillSourceState.Incomplete;
        }
        catch (UnauthorizedAccessException)
        {
            profile.SourceState = LiveSkillSourceState.Incomplete;
        }
    }

    private SourceRegistration CreatePostStartSource(string sourcePath)
    {
        var reader = _sourceReaderFactory(sourcePath);
        reader.CaptureStartOfFileBaseline();
        return new SourceRegistration(reader, null, IsStartupSource: false, CaptureFailed: false);
    }

    private static Stream OpenConsoleStream(string consolePath) => new FileStream(
        consolePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite);

    private static LiveSkillProfileSnapshot CreateSnapshot(ProfileState profile) => new(
        profile.Id,
        profile.Name,
        profile.ActiveSourcePath,
        profile.SourceState,
        profile.Tracker.GetSnapshot())
    {
        ObservationStartedAtUtc = profile.ObservationStartedAtUtc
    };

    private bool IsSuppressed(string? physicalDirectoryKey) => physicalDirectoryKey is not null
        && _suppressions.ContainsKey(physicalDirectoryKey);

    private bool HasOneCurrentCandidate(string physicalDirectoryKey) => _sourceCandidates is not null
        && _sourceCandidates.Count(candidate => candidate.IsCurrent
            && string.Equals(candidate.PhysicalDirectoryKey, physicalDirectoryKey, StringComparison.Ordinal)) == 1;

    private static bool HaveSameSnapshot(LiveSkillProfileSnapshot first, LiveSkillProfileSnapshot second)
    {
        if (!string.Equals(first.ProfileName, second.ProfileName, StringComparison.Ordinal)
            || !string.Equals(first.ActiveSourcePath, second.ActiveSourcePath, StringComparison.OrdinalIgnoreCase)
            || first.SourceState != second.SourceState
            || first.Skills.Count != second.Skills.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Skills.Count; index++)
        {
            if (first.Skills[index] != second.Skills[index])
                return false;
        }

        return true;
    }

    private bool IsValidSourcePath(ProfileState profile, string candidatePath, out string canonicalSourcePath)
    {
        canonicalSourcePath = string.Empty;
        try
        {
            canonicalSourcePath = NormalizePath(candidatePath);
            return SkillsFileNamePattern.IsMatch(Path.GetFileName(canonicalSourcePath))
                && string.Equals(Path.GetDirectoryName(canonicalSourcePath), profile.LogsDirectoryPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static SourceSortKey GetSourceSortKey(string sourcePath)
    {
        var match = SkillsFileNamePattern.Match(Path.GetFileName(sourcePath));
        return new SourceSortKey(
            int.Parse(match.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["month"].Value, System.Globalization.CultureInfo.InvariantCulture),
            match.Groups["day"].Success
                ? int.Parse(match.Groups["day"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0);
    }

    private static string LocatePlayersDirectory()
    {
        var locator = new PlayerDataDirectoryLocator();
        if (!locator.TryGetPlayersDirectoryPath(out var path))
            throw new InvalidOperationException("The Wurm players directory could not be located.");
        return path;
    }

    private static string NormalizePath(string path)
    {
        var normalizedPath = Path.GetFullPath(path.Trim());
        var rootPath = Path.GetPathRoot(normalizedPath);
        return string.Equals(normalizedPath, rootPath, StringComparison.OrdinalIgnoreCase)
            ? normalizedPath
            : normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LiveSkillMonitor));
    }

    private sealed class ProfileState
    {
        public ProfileState(
            ProfileSourceOwner owner,
            string name,
            string logsDirectoryPath,
            string consolePath,
            Func<string, Stream> consoleStreamFactory,
            DateTimeOffset observationStartedAtUtc,
            SkillStateTracker? tracker = null,
            ConsoleTail? consoleTail = null,
            string? presentationRootPath = null,
            string presentationGroupName = "Unknown installation")
        {
            Owner = owner;
            Name = name;
            LogsDirectoryPath = logsDirectoryPath;
            ConsolePath = consolePath;
            ConsoleTail = consoleTail ?? new ConsoleTail(consoleStreamFactory);
            Tracker = tracker ?? new SkillStateTracker();
            ObservationStartedAtUtc = observationStartedAtUtc;
            PresentationRootPath = presentationRootPath;
            PresentationGroupName = presentationGroupName;
        }

        public ProfileSourceOwner Owner { get; }
        public PlayerProfileId Id => Owner.Identity.Id;

        public string Name { get; }
        public string LogsDirectoryPath { get; }
        public string ConsolePath { get; }
        public SkillStateTracker Tracker { get; }
        public Dictionary<string, SourceRegistration> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConsoleTail ConsoleTail { get; }
        public DateTimeOffset ObservationStartedAtUtc { get; }
        public string? PresentationRootPath { get; }
        public string PresentationGroupName { get; }
        public Type? ConsoleFailureKind { get; set; }
        public string? ActiveSourcePath { get; set; }
        public LiveSkillSourceState SourceState { get; set; }
        public bool BootstrapCompleted { get; set; }
        public bool IsInitialized { get; set; }
        public bool Retired { get; set; }
        public bool RequireFreshWriterDeclaration { get; set; }
        public LiveSkillSourceState? SuspendedSourceState { get; set; }
    }

    private sealed class ConsoleTail(Func<string, Stream> streamFactory)
    {
        private readonly List<byte> _pendingBytes = [];
        private MiningSkillsSourceReader.SourceFingerprint _fingerprint = new(0, null);
        private long _position;
        private long _acceptedPosition;
        private long _identityPosition;
        private bool _discardingOversizedRecord;

        public int PendingByteCount => _pendingBytes.Count;

        public bool IsDiscardingOversizedRecord => _discardingOversizedRecord;

        public void DiscardPendingRecord()
        {
            if (_pendingBytes.Count != 0)
                _discardingOversizedRecord = true;
            _pendingBytes.Clear();
        }

        public ConsoleTailReadResult Read(string consolePath)
        {
            using var stream = streamFactory(consolePath);
            var reestablished = stream.Length < _position
                || (_identityPosition > 0
                    && MiningSkillsSourceReader.CreateFingerprint(stream, _identityPosition) != _fingerprint);
            if (reestablished)
                Reset();

            stream.Seek(_position, SeekOrigin.Begin);
            var lines = new List<string>();
            var oversizedRecordStarted = false;
            var buffer = new byte[4096];
            var currentPosition = _position;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var index = 0; index < read; index++)
                {
                    currentPosition++;
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        if (!_discardingOversizedRecord)
                            TryAddCompleteLine(lines);

                        _pendingBytes.Clear();
                        _discardingOversizedRecord = false;
                        _acceptedPosition = currentPosition;
                        continue;
                    }

                    if (_discardingOversizedRecord)
                        continue;

                    if (_pendingBytes.Count == MaximumConsoleRecordLength)
                    {
                        _pendingBytes.Clear();
                        _discardingOversizedRecord = true;
                        oversizedRecordStarted = true;
                        continue;
                    }

                    _pendingBytes.Add(value);
                }
            }

            _position = currentPosition;
            _identityPosition = _pendingBytes.Count > 0 || _discardingOversizedRecord
                ? _position
                : _acceptedPosition;
            _fingerprint = _identityPosition == 0
                ? new MiningSkillsSourceReader.SourceFingerprint(0, null)
                : MiningSkillsSourceReader.CreateFingerprint(stream, _identityPosition);
            return new ConsoleTailReadResult(lines, reestablished, oversizedRecordStarted);
        }

        private void TryAddCompleteLine(List<string> lines)
        {
            try
            {
                var line = StrictUtf8.GetString(_pendingBytes.ToArray());
                if (line.Length > 0 && line[0] == '\uFEFF')
                    line = line[1..];
                lines.Add(line.EndsWith('\r') ? line[..^1] : line);
            }
            catch (DecoderFallbackException)
            {
            }
        }

        private void Reset()
        {
            _pendingBytes.Clear();
            _position = 0;
            _acceptedPosition = 0;
            _identityPosition = 0;
            _fingerprint = new MiningSkillsSourceReader.SourceFingerprint(0, null);
            _discardingOversizedRecord = false;
        }
    }

    private sealed record ConsoleTailReadResult(
        IReadOnlyList<string> CompleteLines,
        bool Reestablished,
        bool OversizedRecordStarted);

    private sealed record SourceRegistration(
        MiningSkillsSourceReader? Reader,
        MiningSkillsSourceBoundary? Boundary,
        bool IsStartupSource,
        bool CaptureFailed);

    private sealed record PendingProfileRegistration(
        ProfileSourceOwner Owner, string ProfileName, string LogsDirectoryPath, string ConsolePath,
        ProfileState? PriorState = null, ConsoleTail? ConsoleTail = null,
        string? PresentationRootPath = null, string PresentationGroupName = "Unknown installation")
    {
        public Type? FailureKind { get; set; }
    }

    private readonly record struct SkillsSuppression(long Generation, long SnapshotVersion);

    private readonly record struct SourceSortKey(int Year, int Month, int Day) : IComparable<SourceSortKey>
    {
        public int CompareTo(SourceSortKey other)
        {
            var year = Year.CompareTo(other.Year);
            if (year != 0) return year;
            var month = Month.CompareTo(other.Month);
            return month != 0 ? month : Day.CompareTo(other.Day);
        }
    }
}
