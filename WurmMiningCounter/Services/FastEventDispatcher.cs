using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public enum EventModule { Mining, Context }

internal sealed record EventTransportProfile(PlayerProfileId Id, string ProfileName);

/// <summary>First unresolved handoff for a terminal profile ingress. Later outputs are not retained.</summary>
public sealed record EventModuleTerminalStatus(
    EventModule Module, PlayerProfileId ProfileId, string ProfileName, string CanonicalPath,
    long SourceGeneration, Exception Failure);

public sealed class EventModuleAdmissionException(EventModule module)
    : Exception($"{module} ingress rejected work; reconciliation is required.");

public sealed class EventModuleTerminalException(EventModuleTerminalStatus status)
    : Exception("Event module ingress requires reconciliation.", status.Failure)
{
    public EventModuleTerminalStatus Status { get; } = status;
}

/// <summary>
/// Serializes handoff and control admission only. Each module independently
/// owns accepted work; terminal rejection is accounted for before Complete is
/// returned to EventPipeline. No domain processing or worker wait occurs under this gate.
/// </summary>
public sealed class FastEventDispatcher
{
    private readonly MiningEventLane _mining;
    private readonly EventLineCoordinator _context;
    private readonly object _sync = new();
    private readonly Dictionary<PlayerProfileId, Profile> _profiles = [];
    private readonly HashSet<Task> _controls = [];
    private readonly Action? _afterMiningAdmission;
    private readonly Action? _beforeProfileReleaseInitiation;
    private readonly BoundedTerminalFaultHistory<Exception> _retainedMiningFailures = new(exception => exception);
    private readonly BoundedTerminalFaultHistory<Exception> _retainedContextFailures = new(exception => exception);
    private Exception? _indeterminateFailure;
    private Task? _completion;
    private bool _closed;

    public FastEventDispatcher(MiningEventLane mining, EventLineCoordinator context)
        : this(mining, context, null) { }

    internal FastEventDispatcher(MiningEventLane mining, EventLineCoordinator context,
        Action? afterMiningAdmission, Action? beforeProfileReleaseInitiation = null)
    {
        _mining = mining ?? throw new ArgumentNullException(nameof(mining));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _afterMiningAdmission = afterMiningAdmission;
        _beforeProfileReleaseInitiation = beforeProfileReleaseInitiation;
        _context.ProfileLaneCreated = RegisterContextProfile;
    }

    public EventPipelineHandoffResult Handoff(EventPipelineOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var (profileId, name, path, generation) = Identity(output);
        lock (_sync)
        {
            if (_closed) return EventPipelineHandoffResult.NotStarted;
            if (_profiles.TryGetValue(profileId, out var releasing) && releasing.Release is not null)
                return EventPipelineHandoffResult.NotStarted;
            var profile = GetProfile(profileId, name, path, generation);
            RefreshFaults(profile);
            try
            {
                if (profile.Mining is null && !_mining.TryEnqueue(output))
                {
                    var fault = GetMiningFault(profile);
                    Record(profile, EventModule.Mining, fault?.UnresolvedOutputs.FirstOrDefault() ?? output,
                        fault ?? (Exception)new EventModuleAdmissionException(EventModule.Mining));
                }

                _afterMiningAdmission?.Invoke();

                if (profile.Context is null)
                {
                    try { _context.AcceptOutput(output); }
                    catch (EventContextLaneFaultException exception)
                    { Record(profile, EventModule.Context, exception.SourceOutput ?? output, exception); }
                    catch (InvalidOperationException exception)
                    { Record(profile, EventModule.Context, output, exception); }
                }
                ObserveFaults(profile);
                return EventPipelineHandoffResult.Complete;
            }
            catch (Exception exception)
            {
                // An unexpected exception after admission cannot be retried.
                _indeterminateFailure ??= exception;
                _closed = true;
                return EventPipelineHandoffResult.Indeterminate;
            }
        }
    }

    public IReadOnlyList<EventModuleTerminalStatus> GetTerminalStatuses()
    {
        lock (_sync)
        {
            foreach (var profile in _profiles.Values) RefreshFaults(profile);
            return _profiles.Values.SelectMany(profile =>
                new[] { profile.Mining, profile.Context }.OfType<EventModuleTerminalStatus>()).ToArray();
        }
    }

    /// <summary>Current dispatcher-owned profiles, used only by session reset orchestration.</summary>
    internal IReadOnlyList<EventTransportProfile> GetKnownProfiles()
    {
        lock (_sync) return _profiles.Values.Select(profile => new EventTransportProfile(profile.Id, profile.Name)).ToArray();
    }

    /// <summary>Published once per module/profile terminal state; observers cannot affect handoff.</summary>
    public event Action<EventModuleTerminalStatus>? TerminalStatusRecorded;

    /// <summary>Published when profile drain/release starts; observers cannot affect release.</summary>
    public event Action<string>? ProfileReleaseStarted;

    internal bool HasProfileLifecycle(PlayerProfileId profileId)
    {
        lock (_sync) return _profiles.ContainsKey(profileId);
    }

    internal (int MiningRetained, long MiningOmitted, int ContextRetained, long ContextOmitted)
        TerminalFaultHistoryCountsForTest
    {
        get
        {
            lock (_sync)
                return (_retainedMiningFailures.RetainedCount, _retainedMiningFailures.OmittedCount,
                    _retainedContextFailures.RetainedCount, _retainedContextFailures.OmittedCount);
        }
    }

    internal Task ResetProfileAsync(PlayerProfileId profileId)
    {
        lock (_sync)
        {
            if (_closed) return Task.FromException(new InvalidOperationException("Dispatcher is completing."));
            if (!_profiles.TryGetValue(profileId, out var profile))
                return Task.FromException(new InvalidOperationException("The Event profile does not exist."));
            if (profile.Release is not null)
                return Task.FromException(new InvalidOperationException("Profile is being released."));
            RefreshFaults(profile);
            var mining = AdmitReset(profile, EventModule.Mining);
            var context = AdmitReset(profile, EventModule.Context);
            ObserveFaults(profile);
            return Track(Task.WhenAll(mining, context));
        }
    }

    /// <summary>
    /// Resets an active profile through both ordered FIFOs, or clears retained
    /// domain state after its release has fully drained. The fallback executes
    /// under dispatcher admission so a concurrent handoff cannot interleave a
    /// new lifecycle between the absence check and the clear.
    /// </summary>
    internal Task ResetProfileOrReleasedAsync(PlayerProfileId profileId, Action resetReleasedProfile)
    {
        ArgumentNullException.ThrowIfNull(resetReleasedProfile);

        lock (_sync)
        {
            if (_closed) return Task.FromException(new InvalidOperationException("Dispatcher is completing."));
            if (!_profiles.TryGetValue(profileId, out var profile))
            {
                try
                {
                    resetReleasedProfile();
                    return Task.CompletedTask;
                }
                catch (Exception exception)
                {
                    return Task.FromException(exception);
                }
            }

            if (profile.Release is not null)
                return ResetAfterReleaseAsync(profileId, profile.Release, resetReleasedProfile);

            RefreshFaults(profile);
            var mining = AdmitReset(profile, EventModule.Mining);
            var context = AdmitReset(profile, EventModule.Context);
            ObserveFaults(profile);
            return Track(Task.WhenAll(mining, context));
        }
    }

    private async Task ResetAfterReleaseAsync(
        PlayerProfileId profileId,
        Task release,
        Action resetReleasedProfile)
    {
        await release.ConfigureAwait(false);
        await ResetProfileOrReleasedAsync(profileId, resetReleasedProfile).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets only the Mining Inspiration state in the selected profile's Mining FIFO.
    /// It shares the normal admission, fault, and release checks without touching Context.
    /// </summary>
    internal Task ResetMiningInspirationAsync(PlayerProfileId profileId)
    {
        lock (_sync)
        {
            if (_closed) return Task.FromException(new InvalidOperationException("Dispatcher is completing."));
            if (!_profiles.TryGetValue(profileId, out var profile))
                return Task.FromException(new InvalidOperationException("The Event profile does not exist."));
            if (profile.Release is not null)
                return Task.FromException(new InvalidOperationException("Profile is being released."));
            RefreshFaults(profile);
            if (profile.Mining is not null)
                return Task.FromException(profile.Mining.Failure);

            Task reset;
            try { reset = ResetMiningInspiration(profile); }
            catch (Exception exception) { reset = Task.FromException(exception); }
            return ObserveResetAsync(profile, EventModule.Mining, reset);
        }
    }

    private Task AdmitReset(Profile profile, EventModule module)
    {
        var status = module == EventModule.Mining ? profile.Mining : profile.Context;
        if (status is not null) return Task.FromException(status.Failure);
        Task reset;
        try
        {
            reset = module == EventModule.Mining
                ? ResetMining(profile)
                : ResetContext(profile);
        }
        catch (Exception exception) { reset = Task.FromException(exception); }
        return ObserveResetAsync(profile, module, reset);
    }

    private async Task ObserveResetAsync(Profile profile, EventModule module, Task reset)
    {
        var resetIdentity = (profile.Id, profile.Name, profile.Path, profile.Generation);
        try { await reset.ConfigureAwait(false); }
        catch (Exception exception)
        {
            lock (_sync) Record(profile, module, null, exception, resetIdentity);
            throw;
        }
    }

    // Suitable for EventPipeline's synchronous lifecycle callback. It only
    // starts cleanup; completion awaits the drain outside the handoff lock.
    internal void ReleaseProfile(PlayerProfileId profileId) => _ = ReleaseProfileAsync(profileId);

    internal Task ReleaseProfileAsync(PlayerProfileId profileId)
    {
        Profile? profile;
        TaskCompletionSource completion;
        Task beginRelease;
        lock (_sync)
        {
            if (!_profiles.TryGetValue(profileId, out profile))
                return Task.CompletedTask;
            if (profile.Release is not null) return profile.Release;
            if (_closed) return _completion ?? Task.FromException(new InvalidOperationException("Dispatcher is completing."));
            RefreshFaults(profile);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            profile.Release = completion.Task; // before either lane can finish/reappear
            beginRelease = InitiateRelease(() =>
            {
                BeginContextRelease(profile);
                return Task.CompletedTask;
            });
            Track(completion.Task);
        }

        // Do not retain the dispatcher gate while Context owns its admission
        // gate: marker creation synchronously registers its profile here.
        var releaseBarrier = InitiateRelease(() =>
        {
            _beforeProfileReleaseInitiation?.Invoke();
            return Task.CompletedTask;
        });
        var mining = Task.WhenAll(releaseBarrier, InitiateRelease(() => ReleaseMining(profile)));
        var context = Task.WhenAll(beginRelease, InitiateRelease(() => ReleaseContext(profile)));
        Notify(ProfileReleaseStarted, profile.Name);
        _ = ReleaseCoreAsync(profile, mining, context, completion);
        return completion.Task;
    }

    private static Task InitiateRelease(Func<Task> initiate)
    {
        try { return initiate(); }
        catch (Exception exception) { return Task.FromException(exception); }
    }

    private async Task ReleaseCoreAsync(Profile profile, Task mining, Task context,
        TaskCompletionSource completion)
    {
        var all = Task.WhenAll(mining, context);
        try { await all.ConfigureAwait(false); }
        catch { /* Both results are accounted for below, after both drains finish. */ }
        lock (_sync)
        {
            RefreshFaults(profile);
            RetainFailures(EventModule.Mining, mining.Exception);
            RetainFailures(EventModule.Context, context.Exception);
            var errors = new List<Exception>();
            AddFailures(profile.Mining?.Failure, errors);
            AddFailures(profile.Context?.Failure, errors);
            AddFailures(all.Exception, errors);
            EndContextRelease(profile);
            if (_profiles.TryGetValue(profile.Id, out var current) && ReferenceEquals(current, profile))
                _profiles.Remove(profile.Id);
            if (errors.Count > 0) completion.TrySetException(new AggregateException(errors).Flatten());
            else completion.TrySetResult();
        }
    }

    public Task CompleteAsync()
    {
        lock (_sync)
        {
            if (_completion is not null) return _completion;
            _closed = true;
            _completion = CompleteCoreAsync(_controls.ToArray(), _mining.CompleteAsync(), _context.CompleteAsync());
            return _completion;
        }
    }

    private async Task CompleteCoreAsync(Task[] controls, Task mining, Task context)
    {
        var all = Task.WhenAll(controls.Append(mining).Append(context));
        try { await all.ConfigureAwait(false); }
        catch { /* Inspect every module failure, not only WhenAll's first exception. */ }
        lock (_sync)
        {
            foreach (var profile in _profiles.Values) RefreshFaults(profile);
            var errors = new List<Exception>();
            AddRetainedFailures(_retainedMiningFailures, EventModule.Mining, errors);
            AddRetainedFailures(_retainedContextFailures, EventModule.Context, errors);
            AddFailures(_indeterminateFailure, errors);
            AddFailures(all.Exception, errors);
            _profiles.Clear();
            _controls.Clear();
            if (errors.Count > 0) throw new AggregateException(errors).Flatten();
        }
    }

    private Task Track(Task task)
    {
        _controls.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception; // faults retained in bounded module status
            lock (_sync) _controls.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private Profile GetProfile(PlayerProfileId profileId, string name, string path, long generation)
    {
        if (!_profiles.TryGetValue(profileId, out var profile))
        {
            profile = new Profile(profileId, name, path, generation);
            _profiles.Add(profileId, profile);
        }
        if (path.Length > 0)
        {
            profile.Path = path;
            profile.Generation = generation;
        }
        return profile;
    }

    private void RegisterContextProfile(PlayerProfileId profileId, string profileName)
    {
        lock (_sync)
        {
            if (_closed)
                return;
            if (_profiles.TryGetValue(profileId, out var existing) && existing.Release is not null)
                return;
            var profile = GetProfile(profileId, profileName, string.Empty, 0);
            ObserveContextFault(profile);
        }
    }

    private void ObserveFaults(Profile profile)
    {
        ObserveMiningFault(profile);
        ObserveContextFault(profile);
    }

    private void ObserveMiningFault(Profile profile)
    {
        if (profile.MiningFaultObserved)
            return;

        profile.MiningFaultObserved = true;
        _ = GetMiningFaulted(profile).ContinueWith(completed =>
        {
            if (completed.Status != TaskStatus.RanToCompletion)
                return;
            lock (_sync) Record(
                profile,
                EventModule.Mining,
                completed.Result.UnresolvedOutputs.FirstOrDefault(),
                completed.Result);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ObserveContextFault(Profile profile)
    {
        if (profile.ContextFaultObserved)
            return;

        profile.ContextFaultObserved = true;
        _ = GetContextFaulted(profile).ContinueWith(completed =>
        {
            if (completed.Status != TaskStatus.RanToCompletion)
                return;
            lock (_sync) Record(profile, EventModule.Context, completed.Result.SourceOutput, completed.Result);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RefreshFaults(Profile profile)
    {
        if (profile.Mining is null && GetMiningFault(profile) is { } mining)
            Record(profile, EventModule.Mining, mining.UnresolvedOutputs.FirstOrDefault(), mining);
        if (profile.Context is null && GetContextFault(profile) is { } context)
            Record(profile, EventModule.Context, context.SourceOutput, context);
    }

    private void Record(Profile profile, EventModule module, EventPipelineOutput? output, Exception failure,
        (PlayerProfileId, string, string, long)? controlIdentity = null)
    {
        // A late reset acknowledgement from a released generation must never
        // close a newly created lane with the same profile name.
        if (!_profiles.TryGetValue(profile.Id, out var current) || !ReferenceEquals(current, profile))
        {
            RetainFailures(module, failure);
            return;
        }
        var identity = controlIdentity ?? (output is null
            ? (profile.Id, profile.Name, profile.Path, profile.Generation) : Identity(output));
        var status = new EventModuleTerminalStatus(module, identity.Item1, identity.Item2, identity.Item3, identity.Item4, failure);
        var recorded = false;
        if (module == EventModule.Mining)
        {
            if (profile.Mining is null)
            {
                profile.Mining = status;
                recorded = true;
                RetainFailures(module, failure);
            }
            CloseMining(profile);
        }
        else
        {
            if (profile.Context is null)
            {
                profile.Context = status;
                recorded = true;
                RetainFailures(module, failure);
            }
            CloseContext(profile);
        }
        if (recorded)
            Notify(TerminalStatusRecorded, status);
    }

    private void RetainFailures(EventModule module, Exception? failure)
    {
        if (failure is null)
            return;

        var history = module == EventModule.Mining ? _retainedMiningFailures : _retainedContextFailures;
        foreach (var exception in FlattenFailures(failure))
            history.Add(exception);
    }

    private static void AddRetainedFailures(
        BoundedTerminalFaultHistory<Exception> history,
        EventModule module,
        List<Exception> target)
    {
        foreach (var failure in history.Snapshot())
            AddFailures(failure, target);
        AddFailures(history.CreateOmittedSummary(module.ToString()), target);
    }

    private static void AddFailures(Exception? failure, List<Exception> target)
    {
        if (failure is null)
            return;

        foreach (var exception in FlattenFailures(failure))
        {
            if (!target.Any(existing => ReferenceEquals(existing, exception)))
                target.Add(exception);
        }
    }

    private static IEnumerable<Exception> FlattenFailures(Exception failure) => failure is AggregateException aggregate
        ? aggregate.Flatten().InnerExceptions
        : [failure];

    private static void Notify<T>(Action<T>? observers, T value)
    {
        if (observers is null)
            return;

        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try { observer(value); }
            catch { /* Diagnostics must not affect dispatcher admission. */ }
        }
    }

    private Task ResetMining(Profile profile) => _mining.ResetProfileAsync(profile.Id, profile.Name);

    private Task ResetMiningInspiration(Profile profile) => _mining.ResetMiningInspirationAsync(profile.Id, profile.Name);

    private Task ResetContext(Profile profile) => _context.ResetProfileAsync(profile.Id);

    private Task ReleaseMining(Profile profile) => _mining.ReleaseProfileAsync(profile.Id);

    private Task ReleaseContext(Profile profile) => _context.ReleaseProfileAsync(profile.Id);

    private void BeginContextRelease(Profile profile)
    {
        _context.BeginProfileRelease(profile.Id);
    }

    private void EndContextRelease(Profile profile)
    {
        _context.EndProfileRelease(profile.Id);
    }

    private MiningLaneFaultException? GetMiningFault(Profile profile) => _mining.GetProfileFault(profile.Id);

    private EventContextLaneFaultException? GetContextFault(Profile profile) => _context.GetProfileFault(profile.Id);

    private Task<MiningLaneFaultException> GetMiningFaulted(Profile profile) => _mining.GetProfileFaulted(profile.Id);

    private Task<EventContextLaneFaultException> GetContextFaulted(Profile profile) => _context.GetProfileFaulted(profile.Id);

    private void CloseMining(Profile profile)
    {
        _mining.CloseProfileAdmission(profile.Id);
    }

    private void CloseContext(Profile profile)
    {
        _context.CloseProfileAdmission(profile.Id);
    }

    private static (PlayerProfileId, string, string, long) Identity(EventPipelineOutput output) => output switch
    {
        EventReadBatch batch => (batch.ProfileId, batch.ProfileName, batch.CanonicalPath, batch.SourceGeneration),
        EventContinuityLoss loss => (loss.ProfileId, loss.ProfileName, loss.CanonicalPath, loss.SourceGeneration),
        _ => throw new ArgumentException("Unknown Event source output.", nameof(output))
    };

    private sealed class Profile(PlayerProfileId id, string name, string path, long generation)
    {
        public PlayerProfileId Id { get; } = id;
        public string Name { get; } = name;
        public string Path { get; set; } = path;
        public long Generation { get; set; } = generation;
        public EventModuleTerminalStatus? Mining { get; set; }
        public EventModuleTerminalStatus? Context { get; set; }
        public Task? Release { get; set; }
        public bool MiningFaultObserved { get; set; }
        public bool ContextFaultObserved { get; set; }
    }
}
