using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed record MiningLaneMetrics(int QueueDepth, int HighWaterMark, bool Faulted, string? FaultType);

/// <summary>
/// An accepted Mining output was not fully processed. The first unresolved output
/// may have a successfully processed prefix. The throwing callback's side effects
/// are indeterminate; later outputs were not dispatched.
/// </summary>
public sealed class MiningLaneFaultException : Exception
{
    internal MiningLaneFaultException(
        PlayerProfileId profileId,
        string profileName,
        int failedLineIndex,
        IReadOnlyList<EventPipelineOutput> unresolvedOutputs,
        Exception cause)
        : base("Mining processing failed after output admission.", cause)
    {
        ProfileId = profileId;
        ProfileName = profileName;
        FailedLineIndex = failedLineIndex;
        UnresolvedOutputs = unresolvedOutputs;
    }

    public PlayerProfileId ProfileId { get; }
    public string ProfileName { get; }
    public int FailedLineIndex { get; }
    public IReadOnlyList<EventPipelineOutput> UnresolvedOutputs { get; }
}

/// <summary>One batch queue and one sequential Mining consumer per profile.</summary>
public sealed class MiningEventLane
{
    internal const int QueueCapacityPerProfile = 64;
    private readonly Action<EventLogLine> _processLine;
    private readonly Action<EventContinuityLoss>? _processContinuity;
    private readonly Action<PlayerProfileId> _resetProfile;
    private readonly Action<PlayerProfileId> _resetMiningInspiration;
    private readonly Action<PlayerProfileId>? _captureResetBoundary;
    private readonly Action<PlayerProfileId, Exception>? _reportResetBoundaryFailure;
    private readonly object _sync = new();
    private readonly Dictionary<PlayerProfileId, ProfileQueue> _queues = [];
    private Task? _completion;
    private bool _completionRequested;
    private readonly BoundedTerminalFaultHistory<MiningLaneFaultException> _terminalFaultHistory =
        new(fault => fault);

    public MiningEventLane(
        MiningEventProcessor processor,
        MiningInspirationBoundaryObserver? boundaryObserver = null,
        Action<PlayerProfileId>? captureResetBoundary = null,
        Action<PlayerProfileId, Exception>? reportResetBoundaryFailure = null)
        : this(
            line => processor.Process(line),
            boundaryObserver is null
                ? null
                : loss => boundaryObserver.Process(new StreamContinuityLost(
                    loss.ProfileId, loss.ProfileName, 0, loss.Reason, loss.CanonicalPath)),
            processor.ResetProfile,
            processor.ResetMiningInspirationProfile,
            captureResetBoundary,
            reportResetBoundaryFailure)
    {
        ArgumentNullException.ThrowIfNull(processor);
    }

    internal MiningEventLane(
        Action<EventLogLine> processLine,
        Action<EventContinuityLoss>? processContinuity = null,
        Action<PlayerProfileId>? resetProfile = null,
        Action<PlayerProfileId>? resetMiningInspiration = null,
        Action<PlayerProfileId>? captureResetBoundary = null,
        Action<PlayerProfileId, Exception>? reportResetBoundaryFailure = null)
    {
        _processLine = processLine ?? throw new ArgumentNullException(nameof(processLine));
        _processContinuity = processContinuity;
        _resetProfile = resetProfile ?? (_ => { });
        _resetMiningInspiration = resetMiningInspiration ?? (_ => { });
        _captureResetBoundary = captureResetBoundary;
        _reportResetBoundaryFailure = reportResetBoundaryFailure;
    }

    internal int ProfileQueueCount
    {
        get { lock (_sync) return _queues.Count; }
    }

    internal bool HasProfileQueue(PlayerProfileId profileId)
    {
        lock (_sync)
            return _queues.ContainsKey(profileId);
    }

    internal (int Retained, long Omitted) TerminalFaultHistoryCountForTest
    {
        get
        {
            lock (_sync) return (_terminalFaultHistory.RetainedCount, _terminalFaultHistory.OmittedCount);
        }
    }

    internal MiningLaneMetrics GetMetrics(PlayerProfileId profileId)
    {
        lock (_sync)
            return _queues.TryGetValue(profileId, out var queue)
                ? queue.GetMetrics()
                : new MiningLaneMetrics(0, 0, false, null);
    }

    // Success transfers the output to this lane, not to a completed callback.
    // A later callback failure closes admission and faults CompleteAsync with
    // the exact outputs whose processing was not confirmed.
    internal bool TryEnqueue(EventPipelineOutput output)
    {
        lock (_sync)
        {
            if (_completionRequested)
                return false;

            return GetOrCreateQueue(GetProfileId(output), GetProfileName(output)).TryEnqueue(output);
        }
    }

    /// <summary>
    /// Enqueues a complete Mining session reset in the selected profile's FIFO.
    /// It returns only after every earlier output has been processed and the
    /// reset has run, before any later output may run.
    /// </summary>
    internal Task ResetProfileAsync(PlayerProfileId profileId, string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        return ResetProfileAsyncCore(profileId, profileName);
    }

    private Task ResetProfileAsyncCore(PlayerProfileId profileId, string profileName)
    {
        lock (_sync)
        {
            if (_queues.TryGetValue(profileId, out var existing) && existing.Fault is { } fault)
                return Task.FromException(fault);
            if (_completionRequested)
                return Task.FromException(new InvalidOperationException("The Mining lane is completing."));

            return GetOrCreateQueue(profileId, profileName).EnqueueProfileReset();
        }
    }

    /// <summary>
    /// Enqueues a per-profile Mining Inspiration reset after every earlier accepted
    /// output and before every later accepted output in the same Mining FIFO.
    /// </summary>
    internal Task ResetMiningInspirationAsync(PlayerProfileId profileId, string profileName) =>
        ResetMiningInspirationAsyncCore(profileId, profileName);

    private Task ResetMiningInspirationAsyncCore(PlayerProfileId profileId, string profileName)
    {
        lock (_sync)
        {
            if (_queues.TryGetValue(profileId, out var existing) && existing.Fault is { } fault)
                return Task.FromException(fault);
            if (_completionRequested)
                return Task.FromException(new InvalidOperationException("The Mining lane is completing."));

            return GetOrCreateQueue(profileId, profileName).EnqueueMiningInspirationReset();
        }
    }

    internal Task<MiningLaneFaultException> GetProfileFaulted(PlayerProfileId profileId) =>
        GetProfileFaultedCore(profileId);

    private Task<MiningLaneFaultException> GetProfileFaultedCore(PlayerProfileId profileId)
    {
        lock (_sync)
        {
            if (!_queues.TryGetValue(profileId, out var queue))
            {
                return Task.FromException<MiningLaneFaultException>(
                    new InvalidOperationException("The Mining profile queue does not exist."));
            }

            return queue.Faulted;
        }
    }

    /// <summary>
    /// Closes one profile's ingress, drains its already accepted work, then
    /// releases its worker and queue metadata. Subsequent admission creates a
    /// fresh lane only after this task completes.
    /// </summary>
    internal Task ReleaseProfileAsync(PlayerProfileId profileId) =>
        ReleaseProfileAsyncCore(profileId);

    private Task ReleaseProfileAsyncCore(PlayerProfileId profileId)
    {

        ProfileQueue? queue;
        lock (_sync)
        {
            if (!_queues.TryGetValue(profileId, out queue))
                return Task.CompletedTask;

            queue.Complete();
        }

        return DrainAndReleaseProfileAsync(profileId, queue);
    }

    public Task CompleteAsync()
    {
        lock (_sync)
        {
            if (_completion is not null)
                return _completion;

            _completionRequested = true;
            var queues = _queues.Values.ToArray();
            foreach (var queue in queues)
                queue.Complete();
            _completion = CompleteCoreAsync(queues);
            return _completion;
        }
    }

    internal MiningLaneFaultException? GetProfileFault(PlayerProfileId profileId)
    {
        lock (_sync)
            return _queues.TryGetValue(profileId, out var queue) ? queue.Fault : null;
    }

    internal void CloseProfileAdmission(PlayerProfileId profileId)
    {
        lock (_sync)
            if (_queues.TryGetValue(profileId, out var queue))
                queue.Complete();
    }

    private ProfileQueue GetOrCreateQueue(PlayerProfileId profileId, string profileName)
    {
        if (_queues.TryGetValue(profileId, out var queue))
            return queue;

        queue = new ProfileQueue(
            profileId, profileName, ProcessOutput, _resetProfile, _resetMiningInspiration, _captureResetBoundary, _reportResetBoundaryFailure,
            RecordTerminalFault);
        _queues.Add(profileId, queue);
        return queue;
    }

    private async Task DrainAndReleaseProfileAsync(PlayerProfileId profileId, ProfileQueue queue)
    {
        try
        {
            await queue.WorkerTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (_queues.TryGetValue(profileId, out var current)
                    && ReferenceEquals(current, queue))
                {
                    _queues.Remove(profileId);
                }
            }
        }
    }

    private async Task CompleteCoreAsync(IReadOnlyList<ProfileQueue> queues)
    {
        Exception? workerFailure = null;
        try
        {
            await Task.WhenAll(queues.Select(queue => queue.WorkerTask)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            workerFailure = exception;
        }

        IReadOnlyList<MiningLaneFaultException> terminalFaults;
        Exception? omittedTerminalFaults;
        lock (_sync)
        {
            terminalFaults = _terminalFaultHistory.Snapshot();
            omittedTerminalFaults = _terminalFaultHistory.CreateOmittedSummary("Mining");
        }

        var failures = new List<Exception>();
        AddFailures(workerFailure, failures);
        foreach (var terminalFault in terminalFaults)
            AddFailures(terminalFault, failures);
        AddFailures(omittedTerminalFaults, failures);
        ThrowFailures(failures);
    }

    private void RecordTerminalFault(MiningLaneFaultException failure)
    {
        lock (_sync)
        {
            _terminalFaultHistory.Add(failure);
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

    private static void ThrowFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
            return;
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    private void ProcessOutput(EventPipelineOutput output)
    {
        switch (output)
        {
            case EventReadBatch batch:
                for (var index = 0; index < batch.Lines.Count; index++)
                {
                    try
                    {
                        _processLine(new EventLogLine(batch.ProfileId, batch.ProfileName, batch.CanonicalPath, batch.Lines[index]));
                    }
                    catch (Exception exception)
                    {
                        throw new OutputProcessingException(index, exception);
                    }
                }
                break;

            case EventContinuityLoss loss:
                try { _processContinuity?.Invoke(loss); }
                catch (Exception exception) { throw new OutputProcessingException(-1, exception); }
                break;
        }
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

    private sealed class OutputProcessingException(int lineIndex, Exception cause)
        : Exception("Mining output callback failed.", cause)
    {
        internal int LineIndex { get; } = lineIndex;
    }

    private sealed class ProfileQueue
    {
        private readonly Channel<QueueEntry> _channel =
            Channel.CreateBounded<QueueEntry>(new BoundedChannelOptions(QueueCapacityPerProfile)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        private readonly Action<EventPipelineOutput> _process;
        private readonly Action<PlayerProfileId> _resetProfile;
        private readonly Action<PlayerProfileId> _resetMiningInspiration;
        private readonly Action<PlayerProfileId>? _captureResetBoundary;
        private readonly Action<PlayerProfileId, Exception>? _reportResetBoundaryFailure;
        private readonly Action<MiningLaneFaultException> _reportFault;
        private int _depth;
        private int _highWater;
        private int _faulted;
        private string? _faultType;
        private MiningLaneFaultException? _fault;
        private readonly TaskCompletionSource<MiningLaneFaultException> _faultObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MiningLaneFaultException? Fault => Volatile.Read(ref _fault);
        public Task<MiningLaneFaultException> Faulted => _faultObserved.Task;

        public ProfileQueue(
            PlayerProfileId profileId,
            string profileName,
            Action<EventPipelineOutput> process,
            Action<PlayerProfileId> resetProfile,
            Action<PlayerProfileId> resetMiningInspiration,
            Action<PlayerProfileId>? captureResetBoundary,
            Action<PlayerProfileId, Exception>? reportResetBoundaryFailure,
            Action<MiningLaneFaultException> reportFault)
        {
            ProfileId = profileId;
            ProfileName = profileName;
            _process = process;
            _resetProfile = resetProfile;
            _resetMiningInspiration = resetMiningInspiration;
            _captureResetBoundary = captureResetBoundary;
            _reportResetBoundaryFailure = reportResetBoundaryFailure;
            _reportFault = reportFault;
            WorkerTask = RunAsync();
        }

        public PlayerProfileId ProfileId { get; }
        public string ProfileName { get; }
        public Task WorkerTask { get; }

        public bool TryEnqueue(EventPipelineOutput output) => TryWrite(new OutputEntry(output));

        public Task EnqueueProfileReset()
        {
            var reset = new ProfileResetEntry();
            if (!TryWrite(reset))
                return Task.FromException(new InvalidOperationException("The Mining profile queue is full or completing."));
            return reset.Completed.Task;
        }

        public Task EnqueueMiningInspirationReset()
        {
            var reset = new MiningInspirationResetEntry();
            if (!TryWrite(reset))
                return Task.FromException(new InvalidOperationException("The Mining profile queue is full or completing."));
            return reset.Completed.Task;
        }

        private bool TryWrite(QueueEntry entry)
        {
            var depth = Interlocked.Increment(ref _depth);
            if (!_channel.Writer.TryWrite(entry))
            {
                Interlocked.Decrement(ref _depth);
                return false;
            }

            if (depth > _highWater)
                _highWater = depth;
            return true;
        }

        public MiningLaneMetrics GetMetrics() => new(
            Volatile.Read(ref _depth),
            Volatile.Read(ref _highWater),
            Volatile.Read(ref _faulted) != 0,
            Volatile.Read(ref _faultType));

        public void Complete() => _channel.Writer.TryComplete();

        private async Task RunAsync()
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _depth);
                ControlEntry? control = entry as ControlEntry;
                try
                {
                    if (entry is OutputEntry outputEntry)
                        _process(outputEntry.Output);
                    else if (entry is ProfileResetEntry)
                    {
                        CaptureResetBoundary();
                        _resetProfile(ProfileId);
                    }
                    else
                        _resetMiningInspiration(ProfileId);

                    control?.Completed.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    var output = (entry as OutputEntry)?.Output;
                    var processingFault = exception as OutputProcessingException;
                    var cause = processingFault?.InnerException ?? exception;
                    Volatile.Write(ref _faultType, cause.GetType().Name);
                    Volatile.Write(ref _faulted, 1);
                    Complete();

                    var unresolved = new List<EventPipelineOutput>();
                    if (output is not null)
                        unresolved.Add(output);
                    var pendingControls = new List<ControlEntry>();
                    while (_channel.Reader.TryRead(out var pending))
                    {
                        Interlocked.Decrement(ref _depth);
                        if (pending is OutputEntry pendingOutput)
                            unresolved.Add(pendingOutput.Output);
                        else if (pending is ControlEntry pendingControl)
                            pendingControls.Add(pendingControl);
                    }

                    var failure = new MiningLaneFaultException(
                        ProfileId,
                        ProfileName,
                        processingFault?.LineIndex ?? -1,
                        unresolved.AsReadOnly(),
                        cause);
                    Volatile.Write(ref _fault, failure);
                    _faultObserved.TrySetResult(failure);
                    control?.Completed.TrySetException(failure);
                    foreach (var reset in pendingControls)
                        reset.Completed.TrySetException(failure);
                    _reportFault(failure);
                    throw failure;
                }
            }
        }

        private void CaptureResetBoundary()
        {
            try
            {
                _captureResetBoundary?.Invoke(ProfileId);
            }
            catch (Exception exception)
            {
                try { _reportResetBoundaryFailure?.Invoke(ProfileId, exception); }
                catch { System.Diagnostics.Trace.TraceError("Mining reset-boundary failure reporting failed."); }
            }
        }

        private abstract class QueueEntry;

        private abstract class ControlEntry : QueueEntry
        {
            internal TaskCompletionSource<bool> Completed { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class OutputEntry(EventPipelineOutput output) : QueueEntry
        {
            internal EventPipelineOutput Output { get; } = output;
        }

        private sealed class ProfileResetEntry : ControlEntry;

        private sealed class MiningInspirationResetEntry : ControlEntry;
    }
}
