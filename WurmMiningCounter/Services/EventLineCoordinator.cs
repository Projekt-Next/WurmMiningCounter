using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using System.Diagnostics;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed record EventCoordinatorQueueMetrics(
    int QueueDepth,
    int HighWaterMark,
    bool Faulted = false,
    string? FaultType = null);

public enum EventContextLaneFaultKind
{
    QueueOverflow,
    ConsumerFault
}

/// <summary>
/// The ordered Context stream for one profile cannot safely continue without
/// reconciliation. FirstUnresolvedItems identifies the first entry whose
/// processing was not conclusively completed; later rejected entries are not
/// retained.
/// </summary>
public sealed class EventContextLaneFaultException : Exception
{
    internal EventContextLaneFaultException(
        PlayerProfileId profileId,
        string profileName,
        EventContextLaneFaultKind kind,
        IReadOnlyList<OrderedEventItem> firstUnresolvedItems,
        Exception? cause = null,
        EventPipelineOutput? sourceOutput = null)
        : base("Ordered Context processing requires reconciliation.", cause)
    {
        ProfileId = profileId;
        ProfileName = profileName;
        Kind = kind;
        FirstUnresolvedItems = firstUnresolvedItems;
        SourceOutput = sourceOutput;
    }

    public PlayerProfileId ProfileId { get; }
    public string ProfileName { get; }
    public EventContextLaneFaultKind Kind { get; }
    public IReadOnlyList<OrderedEventItem> FirstUnresolvedItems { get; }
    public EventPipelineOutput? SourceOutput { get; }
}

public sealed class EventLineCoordinator : IDisposable
{
    private static readonly AsyncLocal<EventLineCoordinator?> CurrentWorkerCoordinator = new();
    internal const int QueueCapacityPerProfile = 64;

    private readonly object _sync = new();
    private readonly object _lifecycleSync = new();
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private readonly Dictionary<PlayerProfileId, ProfileQueue> _profileQueues = [];
    private readonly HashSet<PlayerProfileId> _releasingProfiles = [];
    private readonly List<IOrderedEventConsumer> _consumers = [];
    private readonly TaskCompletionSource<bool> _admissionOperationsDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _completionRequested;
    private TaskCompletionSource<bool>? _completion;
    private Task? _resourceDisposalTask;
    private int _activeAdmissionOperations;
    private int _disposeRequested;
    private int _synchronizationDisposed;
    private readonly BoundedTerminalFaultHistory<EventContextLaneFaultException> _releasedProfileFaultHistory =
        new(fault => fault);

    // The target EventPipeline dispatcher owns profile lifecycle. Context can also create
    // a lane for marker-only work, so it reports that creation synchronously.
    internal Action<PlayerProfileId, string>? ProfileLaneCreated { get; set; }

    // Test-only rendezvous for proving that profile registration is never
    // invoked while Context owns an admission or state lock.
    internal Action<string>? BeforeProfileLaneRegistration { get; set; }

    // Test-only rendezvous after a released lane has been removed.
    internal Action<string>? ProfileLaneReleased { get; set; }
    internal Action<PlayerProfileId>? ProfileLaneDrained { get; set; }

    public EventLineCoordinator() { }

    internal int ProfileQueueCount
    {
        get { lock (_sync) return _profileQueues.Count; }
    }

    internal bool HasProfileQueue(PlayerProfileId profileId)
    {
        lock (_sync)
            return _profileQueues.ContainsKey(profileId);
    }

    internal (int Retained, long Omitted) ReleasedProfileFaultHistoryCountForTest
    {
        get
        {
            lock (_sync) return (_releasedProfileFaultHistory.RetainedCount, _releasedProfileFaultHistory.OmittedCount);
        }
    }

    internal EventCoordinatorQueueMetrics GetQueueMetrics(PlayerProfileId profileId)
    {
        lock (_sync)
            return _profileQueues.TryGetValue(profileId, out var queue)
                ? queue.GetMetrics()
                : new EventCoordinatorQueueMetrics(0, 0, false, null);
    }

    internal EventContextLaneFaultException? GetProfileFault(PlayerProfileId profileId)
    {
        lock (_sync)
            return _profileQueues.TryGetValue(profileId, out var queue)
                ? queue.Fault
                : null;
    }

    internal Task<EventContextLaneFaultException> GetProfileFaulted(PlayerProfileId profileId) =>
        GetProfileFaultedCore(profileId);

    private Task<EventContextLaneFaultException> GetProfileFaultedCore(PlayerProfileId profileId)
    {
        lock (_sync)
        {
            if (!_profileQueues.TryGetValue(profileId, out var queue))
            {
                return Task.FromException<EventContextLaneFaultException>(
                    new InvalidOperationException("The Context profile queue does not exist."));
            }

            return queue.Faulted;
        }
    }

    public void RegisterConsumer(IOrderedEventConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        EnterAdmission();
        try
        {
            lock (_sync)
            {
                if (_consumers.Any(existing => ReferenceEquals(existing, consumer)))
                    return;

                _consumers.Add(consumer);
            }
        }
        finally
        {
            ExitAdmission();
        }
    }

    public void UnregisterConsumer(IOrderedEventConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        EnterAdmission();
        try
        {
            lock (_sync)
                _consumers.RemoveAll(existing => ReferenceEquals(existing, consumer));
        }
        finally
        {
            ExitAdmission();
        }
    }

    public async Task EnqueueProfileMarkerAsync(
        PlayerProfileId profileId,
        string profileName,
        Func<PlayerProfileId, string, long, OrderedEventItem> createMarker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(createMarker);

        Task completion;
        var created = false;
        EnterAdmission();
        try
        {
            ProfileQueue profileQueue;
            lock (_sync)
            {
                ThrowIfProfileReleaseActive(profileId);
                profileQueue = GetOrCreateProfileQueue(profileId, profileName, out created);
            }

            completion = profileQueue.EnqueueMarker(createMarker);
        }
        finally
        {
            ExitAdmission();
            if (created)
                NotifyProfileLaneCreated(profileId, profileName);
        }

        await completion.ConfigureAwait(false);
    }

    // EventPipeline module ingress: retain the whole output as one queue entry. Line
    // adapters are created only by the profile worker, after handoff returns.
    internal void AcceptOutput(EventPipelineOutput output)
    {
        var profileId = GetProfileId(output);
        var profileName = GetProfileName(output);
        var created = false;
        EnterAdmission();
        try
        {
            ProfileQueue queue;
            lock (_sync)
            {
                ThrowIfProfileReleaseActive(profileId);
                queue = GetOrCreateProfileQueue(profileId, profileName, out created);
            }
            queue.EnqueueOutput(output);
        }
        finally
        {
            ExitAdmission();
            if (created)
                NotifyProfileLaneCreated(profileId, profileName);
        }
    }

    internal void CloseProfileAdmission(PlayerProfileId profileId)
    {
        lock (_sync)
            if (_profileQueues.TryGetValue(profileId, out var queue))
                queue.Complete();
    }

    internal Task ResetProfileAsync(PlayerProfileId profileId) =>
        ResetProfileAsyncCore(profileId);

    private async Task ResetProfileAsyncCore(PlayerProfileId profileId)
    {
        Task? completion = null;
        IOrderedEventConsumer[]? releasedProfileConsumers = null;
        EnterAdmission();
        try
        {
            ProfileQueue? profileQueue;
            lock (_sync)
            {
                ThrowIfProfileReleaseActive(profileId);
                _profileQueues.TryGetValue(profileId, out profileQueue);
                if (profileQueue is null)
                    releasedProfileConsumers = _consumers.ToArray();
            }

            // A Context reset is a control for an existing ordered lane.  It
            // must not manufacture a lifecycle with an empty display name
            // after the Event source was released.
            if (profileQueue is not null)
                completion = profileQueue.EnqueueResetProfileBarrier().Completed.Task;
            else if (releasedProfileConsumers is not null)
            {
                // The released lane has no FIFO left to serialize against.
                // Keep admission closed while clearing retained consumer
                // state, so a new handoff cannot be cleared out of order.
                foreach (var consumer in releasedProfileConsumers)
                    consumer.Invalidate(profileId, string.Empty);
            }
        }
        finally
        {
            ExitAdmission();
        }

        if (completion is not null)
            await completion.ConfigureAwait(false);
    }

    /// <summary>
    /// Closes one profile's ingress, drains its already admitted FIFO work, and
    /// releases its worker and queue metadata. A later admission for the same
    /// profile creates a fresh lane only after this task has completed.
    /// </summary>
    internal Task ReleaseProfileAsync(PlayerProfileId profileId) =>
        ReleaseProfileAsyncCore(profileId);

    private Task ReleaseProfileAsyncCore(PlayerProfileId profileId)
    {
        ProfileQueue? profileQueue;
        EnterAdmission();
        try
        {
            lock (_sync)
                _profileQueues.TryGetValue(profileId, out profileQueue);

            profileQueue?.Complete();
        }
        finally
        {
            ExitAdmission();
        }

        return profileQueue is null
            ? Task.CompletedTask
            : DrainAndReleaseProfileAsync(profileId, profileQueue);
    }

    // Called by the dispatcher while it establishes a profile release. The
    // admission gate makes this a linearization point with marker/control
    // admission, so a released profile cannot recreate a Context lane.
    internal void BeginProfileRelease(PlayerProfileId profileId) =>
        BeginProfileReleaseCore(profileId);

    private void BeginProfileReleaseCore(PlayerProfileId profileId)
    {
        EnterAdmission();
        try
        {
            lock (_sync)
                _releasingProfiles.Add(profileId);
        }
        finally
        {
            ExitAdmission();
        }
    }

    internal void EndProfileRelease(PlayerProfileId profileId) =>
        EndProfileReleaseCore(profileId);

    private void EndProfileReleaseCore(PlayerProfileId profileId)
    {
        lock (_sync)
            _releasingProfiles.Remove(profileId);
    }

    public Task CompleteAsync()
    {
        TaskCompletionSource<bool> completion;
        var startCompletion = false;

        lock (_lifecycleSync)
        {
            if (_completion is not null)
                return _completion.Task;

            _completionRequested = true;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion;
            if (_activeAdmissionOperations == 0)
                _admissionOperationsDrained.TrySetResult(true);

            startCompletion = true;
        }

        if (startCompletion)
            _ = CompleteCoreAsync(completion);

        return completion.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        var completion = CompleteAsync();
        if (ReferenceEquals(CurrentWorkerCoordinator.Value, this))
        {
            ScheduleSynchronizationDisposal(completion);
            GC.SuppressFinalize(this);
            return;
        }

        try
        {
            completion.GetAwaiter().GetResult();
        }
        finally
        {
            DisposeSynchronizationPrimitives();
        }

        GC.SuppressFinalize(this);
    }

    private async Task CompleteCoreAsync(TaskCompletionSource<bool> completion)
    {
        Exception? workerException = null;
        try
        {
            await _admissionOperationsDrained.Task.ConfigureAwait(false);
            await _admissionGate.WaitAsync().ConfigureAwait(false);
            ProfileQueue[] profileQueues;
            try
            {
                lock (_sync)
                {
                    profileQueues = _profileQueues.Values.ToArray();
                }

                foreach (var profileQueue in profileQueues)
                    profileQueue.Complete();
            }
            finally
            {
                _admissionGate.Release();
            }

            try
            {
                await Task.WhenAll(profileQueues.Select(profileQueue => profileQueue.WorkerTask)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                workerException = exception;
            }

            IReadOnlyList<EventContextLaneFaultException> releasedProfileFaults;
            Exception? omittedReleasedProfileFaults;
            lock (_sync)
            {
                releasedProfileFaults = _releasedProfileFaultHistory.Snapshot();
                omittedReleasedProfileFaults = _releasedProfileFaultHistory.CreateOmittedSummary("Context");
            }

            var failures = new List<Exception>();
            AddFailures(workerException, failures);
            foreach (var releasedProfileFault in releasedProfileFaults)
                AddFailures(releasedProfileFault, failures);
            AddFailures(omittedReleasedProfileFaults, failures);
            if (failures.Count == 0)
                completion.TrySetResult(true);
            else if (failures.Count == 1)
                completion.TrySetException(failures[0]);
            else
                completion.TrySetException(new AggregateException(failures));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DrainAndReleaseProfileAsync(PlayerProfileId profileId, ProfileQueue profileQueue)
    {
        try
        {
            await profileQueue.WorkerTask.ConfigureAwait(false);
        }
        catch (EventContextLaneFaultException fault)
        {
            lock (_sync)
            {
                _releasedProfileFaultHistory.Add(fault);
            }
            throw;
        }
        finally
        {
            var removed = false;
            lock (_sync)
            {
                if (_profileQueues.TryGetValue(profileId, out var current)
                    && ReferenceEquals(current, profileQueue))
                {
                    _profileQueues.Remove(profileId);
                    removed = true;
                }
            }

            if (removed)
            {
                ProfileLaneDrained?.Invoke(profileId);
                ProfileLaneReleased?.Invoke(profileQueue.ProfileName);
            }
        }
    }

    // Called only under _sync. The callback is deliberately deferred until
    // after the caller releases both _sync and _admissionGate: dispatcher
    // registration reads Context state and therefore must never invert those
    // locks with dispatcher handoff.
    private ProfileQueue GetOrCreateProfileQueue(PlayerProfileId profileId, string profileName, out bool created)
    {
        if (_profileQueues.TryGetValue(profileId, out var profileQueue))
        {
            created = false;
            return profileQueue;
        }

        profileQueue = new ProfileQueue(this, profileId, profileName, ProcessItem);
        _profileQueues.Add(profileId, profileQueue);
        created = true;
        return profileQueue;
    }

    private void NotifyProfileLaneCreated(PlayerProfileId profileId, string profileName)
    {
        BeforeProfileLaneRegistration?.Invoke(profileName);
        ProfileLaneCreated?.Invoke(profileId, profileName);
    }

    private void ThrowIfProfileReleaseActive(PlayerProfileId profileId)
    {
        if (_releasingProfiles.Contains(profileId))
            throw new InvalidOperationException("The Context profile is being released.");
    }

    private static string GetProfileName(EventPipelineOutput output) => output switch
    {
        EventReadBatch batch => batch.ProfileName,
        EventContinuityLoss loss => loss.ProfileName,
        _ => throw new ArgumentException("Unknown Event source output.", nameof(output))
    };

    private static PlayerProfileId GetProfileId(EventPipelineOutput output) => output switch
    {
        EventReadBatch batch => batch.ProfileId,
        EventContinuityLoss loss => loss.ProfileId,
        _ => throw new ArgumentException("Unknown Event source output.", nameof(output))
    };

    private void ProcessItem(ProfileQueue profileQueue, OrderedEventItem item)
    {
        IOrderedEventConsumer[] consumers;
        lock (_sync)
        {
            consumers = _consumers.ToArray();
        }

        foreach (var consumer in consumers)
        {
            if (item is StreamContinuityLost or ResetProfileBarrier)
                consumer.Invalidate(item.ProfileId, item.ProfileName);

            if (item is OrderedEventLine orderedEventLine)
            {
                Debug.WriteLine(
                    $"[COORD] consumer={consumer.GetType().Name} profile={orderedEventLine.ProfileName} seq={orderedEventLine.SequenceNumber} text={orderedEventLine.Line.Text}");
            }

            consumer.Process(item);
        }
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

    private void EnterAdmission()
    {
        BeginAdmissionOperation();
        var gateEntered = false;
        try
        {
            _admissionGate.Wait();
            gateEntered = true;
        }
        catch
        {
            if (gateEntered)
                _admissionGate.Release();

            EndAdmissionOperation();
            throw;
        }
    }

    private void ExitAdmission()
    {
        _admissionGate.Release();
        EndAdmissionOperation();
    }

    private void BeginAdmissionOperation()
    {
        lock (_lifecycleSync)
        {
            ThrowIfCompletionRequested();
            _activeAdmissionOperations++;
        }
    }

    private void EndAdmissionOperation()
    {
        lock (_lifecycleSync)
        {
            _activeAdmissionOperations--;
            if (_completionRequested && _activeAdmissionOperations == 0)
                _admissionOperationsDrained.TrySetResult(true);
        }
    }

    private void ThrowIfCompletionRequested()
    {
        lock (_lifecycleSync)
        {
            if (_completionRequested)
                throw new InvalidOperationException("The event line coordinator is completing.");
        }
    }

    private void ScheduleSynchronizationDisposal(Task completion)
    {
        lock (_lifecycleSync)
        {
            _resourceDisposalTask ??= DisposeSynchronizationPrimitivesAfterCompletionAsync(completion);
        }
    }

    private async Task DisposeSynchronizationPrimitivesAfterCompletionAsync(Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            DisposeSynchronizationPrimitives();
        }
    }

    private void DisposeSynchronizationPrimitives()
    {
        if (Interlocked.Exchange(ref _synchronizationDisposed, 1) != 0)
            return;

        _admissionGate.Dispose();
    }

    private sealed class ProfileQueue
    {
        private readonly EventLineCoordinator _coordinator;
        private readonly object _ingressSync = new();
        private readonly Channel<QueueEntry> _channel = Channel.CreateBounded<QueueEntry>(
            new BoundedChannelOptions(QueueCapacityPerProfile)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        private readonly Action<ProfileQueue, OrderedEventItem> _processItem;
        private long _nextSequenceNumber;
        private int _depth;
        private int _highWater;
        private bool _writerCompletionRequested;
        private EventContextLaneFaultException? _fault;
        private readonly Queue<QueueEntry> _pendingEntries = [];
        private QueueEntry? _processingEntry;
        private readonly TaskCompletionSource<EventContextLaneFaultException> _faultObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProfileQueue(
            EventLineCoordinator coordinator,
            PlayerProfileId profileId,
            string profileName,
            Action<ProfileQueue, OrderedEventItem> processItem)
        {
            _coordinator = coordinator;
            ProfileId = profileId;
            ProfileName = profileName;
            _processItem = processItem;
            WorkerTask = ProcessQueueAsync();
        }

        public PlayerProfileId ProfileId { get; }
        public string ProfileName { get; }

        public Task WorkerTask { get; }

        public EventContextLaneFaultException? Fault
        {
            get { lock (_ingressSync) return _fault; }
        }

        public Task<EventContextLaneFaultException> Faulted => _faultObserved.Task;

        public void EnqueueOutput(EventPipelineOutput output)
        {
            lock (_ingressSync)
            {
                ThrowIfUnavailable();
                var count = output is EventReadBatch batch ? Math.Max(1, batch.Lines.Count) : 1;
                var last = GetLastSequenceNumberAfter(count);
                WriteOrFault(new OutputEntry(output, checked(_nextSequenceNumber + 1)));
                _nextSequenceNumber = last;
            }
        }

        public ResetProfileBarrierEntry EnqueueResetProfileBarrier()
        {
            lock (_ingressSync)
            {
                ThrowIfUnavailable();
                var sequenceNumber = GetLastSequenceNumberAfter(1);
                var barrierEntry = new ResetProfileBarrierEntry(new ResetProfileBarrier(
                    ProfileId,
                    ProfileName,
                    sequenceNumber));
                WriteOrFault(barrierEntry);
                _nextSequenceNumber = sequenceNumber;
                return barrierEntry;
            }
        }

        public Task EnqueueMarker(Func<PlayerProfileId, string, long, OrderedEventItem> createMarker)
        {
            lock (_ingressSync)
            {
                ThrowIfUnavailable();
                var sequenceNumber = GetLastSequenceNumberAfter(1);
                var marker = createMarker(ProfileId, ProfileName, sequenceNumber)
                    ?? throw new InvalidOperationException("The profile marker cannot be null.");
                if (marker.ProfileId != ProfileId
                    || !string.Equals(marker.ProfileName, ProfileName, StringComparison.Ordinal)
                    || marker.SequenceNumber != sequenceNumber)
                {
                    throw new InvalidOperationException("The profile marker must retain its assigned profile and sequence number.");
                }

                var markerEntry = new MarkerEntry(marker);
                WriteOrFault(markerEntry);
                _nextSequenceNumber = sequenceNumber;
                return markerEntry.Completed.Task;
            }
        }

        public void Complete()
        {
            lock (_ingressSync)
            {
                _writerCompletionRequested = true;
                _channel.Writer.TryComplete();
            }
        }

        public EventCoordinatorQueueMetrics GetMetrics()
        {
            var fault = Fault;
            return new EventCoordinatorQueueMetrics(
                Volatile.Read(ref _depth),
                Volatile.Read(ref _highWater),
                fault is not null,
                fault?.Kind.ToString());
        }

        private void ThrowIfUnavailable()
        {
            if (_fault is not null)
                throw _fault;
            if (_writerCompletionRequested)
                throw new InvalidOperationException("The profile queue is completing.");
        }

        private void WriteOrFault(QueueEntry entry)
        {
            if (TryWrite(entry))
                return;

            throw SetFaultLocked(entry, EventContextLaneFaultKind.QueueOverflow, null);
        }

        private bool TryWrite(QueueEntry entry)
        {
            var depth = Interlocked.Increment(ref _depth);
            if (!_channel.Writer.TryWrite(entry))
            {
                Interlocked.Decrement(ref _depth);
                return false;
            }

            _pendingEntries.Enqueue(entry);
            if (depth > _highWater)
                Volatile.Write(ref _highWater, depth);
            return true;
        }

        private EventContextLaneFaultException SetFault(
            QueueEntry entry,
            EventContextLaneFaultKind kind,
            Exception? cause)
        {
            lock (_ingressSync)
                return SetFaultLocked(entry, kind, cause);
        }

        private EventContextLaneFaultException SetFaultLocked(
            QueueEntry entry,
            EventContextLaneFaultKind kind,
            Exception? cause)
        {
            if (_fault is not null)
                return _fault;

            var firstUnresolved = _processingEntry ?? (_pendingEntries.TryPeek(out var pending) ? pending : entry);
            _fault = new EventContextLaneFaultException(
                ProfileId,
                ProfileName,
                kind,
                GetItems(firstUnresolved),
                cause,
                (firstUnresolved as OutputEntry)?.Output);
            _faultObserved.TrySetResult(_fault);
            _writerCompletionRequested = true;
            _channel.Writer.TryComplete();
            return _fault;
        }

        private async Task ProcessQueueAsync()
        {
            var previousCoordinator = CurrentWorkerCoordinator.Value;
            CurrentWorkerCoordinator.Value = _coordinator;
            try
            {
                await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    Interlocked.Decrement(ref _depth);
                    try
                    {
                        var existingFault = BeginProcessing(entry);
                        if (existingFault is not null)
                        {
                            FailEntry(entry, existingFault);
                            DrainFaultedEntries(existingFault);
                            throw existingFault;
                        }

                        await ProcessEntryAsync(entry).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        var fault = SetFault(entry, EventContextLaneFaultKind.ConsumerFault, exception);
                        FailEntry(entry, fault);
                        DrainFaultedEntries(fault);
                        throw fault;
                    }
                    finally
                    {
                        EndProcessing(entry);
                    }
                }

                if (Fault is { } terminalFault)
                    throw terminalFault;
            }
            finally
            {
                CurrentWorkerCoordinator.Value = previousCoordinator;
            }
        }

        private async Task ProcessEntryAsync(QueueEntry entry)
        {
            switch (entry)
            {
                case OutputEntry outputEntry:
                    foreach (var item in ExpandOutput(outputEntry))
                        _processItem(this, item);
                    return;
                case ResetProfileBarrierEntry resetProfileBarrierEntry:
                    _processItem(this, resetProfileBarrierEntry.Barrier);
                    resetProfileBarrierEntry.Completed.TrySetResult(true);
                    return;

                case MarkerEntry markerEntry:
                    _processItem(this, markerEntry.Marker);
                    markerEntry.Completed.TrySetResult(true);
                    return;

                default:
                    throw new InvalidOperationException("Unknown ordered Context queue entry.");
            }
        }

        private void DrainFaultedEntries(EventContextLaneFaultException fault)
        {
            while (_channel.Reader.TryRead(out var pending))
            {
                Interlocked.Decrement(ref _depth);
                RemovePendingEntry(pending);
                FailEntry(pending, fault);
            }
        }

        private EventContextLaneFaultException? BeginProcessing(QueueEntry entry)
        {
            lock (_ingressSync)
            {
                if (_pendingEntries.Count == 0 || !ReferenceEquals(_pendingEntries.Peek(), entry))
                {
                    throw new InvalidOperationException("The ordered Context queue lost FIFO entry tracking.");
                }

                _pendingEntries.Dequeue();
                _processingEntry = entry;
                return _fault;
            }
        }

        private void EndProcessing(QueueEntry entry)
        {
            lock (_ingressSync)
            {
                if (ReferenceEquals(_processingEntry, entry))
                    _processingEntry = null;
            }
        }

        private void RemovePendingEntry(QueueEntry entry)
        {
            lock (_ingressSync)
            {
                if (_pendingEntries.Count == 0 || !ReferenceEquals(_pendingEntries.Peek(), entry))
                {
                    throw new InvalidOperationException("The ordered Context queue lost FIFO entry tracking.");
                }

                _pendingEntries.Dequeue();
            }
        }

        private static void FailEntry(QueueEntry entry, EventContextLaneFaultException fault)
        {
            switch (entry)
            {
                case ResetProfileBarrierEntry reset:
                    reset.Completed.TrySetException(fault);
                    break;
                case MarkerEntry marker:
                    marker.Completed.TrySetException(fault);
                    break;
            }
        }

        private static IReadOnlyList<OrderedEventItem> GetItems(QueueEntry entry) => entry switch
        {
            OutputEntry output => ExpandOutput(output).ToArray(),
            ResetProfileBarrierEntry reset => [reset.Barrier],
            MarkerEntry marker => [marker.Marker],
            _ => []
        };

        private static IEnumerable<OrderedEventItem> ExpandOutput(OutputEntry entry)
        {
            if (entry.Output is EventReadBatch batch)
            {
                for (var index = 0; index < batch.Lines.Count; index++)
                    yield return new OrderedEventLine(batch.ProfileId, batch.ProfileName, entry.FirstSequence + index,
                        new EventLogLine(batch.ProfileId, batch.ProfileName, batch.CanonicalPath, batch.Lines[index]));
            }
            else if (entry.Output is EventContinuityLoss loss)
                yield return new StreamContinuityLost(loss.ProfileId, loss.ProfileName, entry.FirstSequence,
                    loss.Reason, loss.CanonicalPath);
        }

        private long GetLastSequenceNumberAfter(int itemCount)
            => checked(_nextSequenceNumber + itemCount);
    }

    private abstract class QueueEntry;

    private sealed class OutputEntry(EventPipelineOutput output, long firstSequence) : QueueEntry
    {
        public EventPipelineOutput Output { get; } = output;
        public long FirstSequence { get; } = firstSequence;
    }

    private sealed class ResetProfileBarrierEntry(ResetProfileBarrier barrier) : QueueEntry
    {
        public ResetProfileBarrier Barrier { get; } = barrier;

        public TaskCompletionSource<bool> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class MarkerEntry(OrderedEventItem marker) : QueueEntry
    {
        public OrderedEventItem Marker { get; } = marker;

        public TaskCompletionSource<bool> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
