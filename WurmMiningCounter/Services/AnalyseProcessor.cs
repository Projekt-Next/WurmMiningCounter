using System.Collections.Concurrent;
using System.Collections.Immutable;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class AnalyseProcessor : IOrderedEventConsumer
{
    private readonly ConcurrentDictionary<PlayerProfileId, ProfileState> _states = [];
    private readonly SessionBoundaryParser _sessionBoundaryParser = new();

    public event EventHandler<AnalyseChangedEventArgs>? AnalyseChanged;

    public void Invalidate(PlayerProfileId profileId, string profileName) => ClearProfile(profileId);

    public void Process(OrderedEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is StreamContinuityLost or ResetProfileBarrier)
        {
            ClearProfile(item.ProfileId);
            return;
        }
        if (item is not OrderedEventLine line)
            return;
        if (_sessionBoundaryParser.IsLoggingStarted(line.Line.Text) || _sessionBoundaryParser.IsReconnect(line.Line.Text))
        {
            ClearProfile(item.ProfileId);
            return;
        }

        if (!_states.TryGetValue(item.ProfileId, out var state))
        {
            if (!AnalyseLineParser.IsAnalyseStart(line.Line.Text)) return;
            state = _states.GetOrAdd(item.ProfileId, static _ => new ProfileState());
        }
        AnalyseSnapshot? snapshot = null;
        lock (state.Sync)
        {
            if (state.Assembler.Process(line.Line.Text) is { } result)
                state.Snapshot = snapshot = new AnalyseSnapshot(item.ProfileId, item.ProfileName, result);
        }
        if (snapshot is not null)
            AnalyseChanged?.Invoke(this, new AnalyseChangedEventArgs(item.ProfileId, snapshot));
    }

    public ImmutableArray<AnalyseSnapshot> GetSnapshots() => _states.Values.Select(state =>
    {
        lock (state.Sync) return state.Snapshot;
    }).OfType<AnalyseSnapshot>().ToImmutableArray();

    internal ImmutableArray<PlayerProfileId> GetProfiles() => _states.Keys.ToImmutableArray();

    internal void ResetProfile(PlayerProfileId profileId) => ClearProfile(profileId);

    // A released lane keeps its last completed snapshot, but cannot carry an
    // unfinished sequence into a later lane for the same profile.
    internal void ReleaseProfile(PlayerProfileId profileId)
    {
        if (!_states.TryGetValue(profileId, out var state)) return;
        lock (state.Sync) state.Assembler.Reset();
    }

    private void ClearProfile(PlayerProfileId profileId)
    {
        if (!_states.TryGetValue(profileId, out var state)) return;
        bool hadSnapshot;
        lock (state.Sync)
        {
            state.Assembler.Reset();
            hadSnapshot = state.Snapshot is not null;
            state.Snapshot = null;
        }
        if (hadSnapshot)
            AnalyseChanged?.Invoke(this, new AnalyseChangedEventArgs(profileId, null));
    }

    private sealed class ProfileState
    {
        public object Sync { get; } = new();
        public AnalyseSequenceAssembler Assembler { get; } = new();
        public AnalyseSnapshot? Snapshot { get; set; }
    }
}
