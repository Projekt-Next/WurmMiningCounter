using System.Collections.Concurrent;
using System.Collections.Immutable;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class NearbyScanProcessor : IOrderedEventConsumer
{
    private readonly ConcurrentDictionary<PlayerProfileId, ProfileNearbyState> _profileStates = [];
    private readonly SessionBoundaryParser _sessionBoundaryParser = new();
    private readonly RuntimeDiagnosticService _diagnostics;

    public NearbyScanProcessor(RuntimeDiagnosticService? diagnostics = null)
    {
        _diagnostics = diagnostics ?? new RuntimeDiagnosticService();
    }

    public event EventHandler<NearbyScanChangedEventArgs>? NearbyScanChanged;

    public void Invalidate(PlayerProfileId profileId, string profileName)
    {
        ClearProfile(profileId);
    }

    public void Process(OrderedEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        switch (item)
        {
            case OrderedEventLine line:
                ProcessLine(line);
                break;
            case StreamContinuityLost:
            case ResetProfileBarrier:
                ClearProfile(item.ProfileId);
                break;
        }
    }

    public ImmutableArray<NearbyScanSnapshot> GetSnapshots() =>
        _profileStates.Values
            .Select(state =>
            {
                lock (state.Sync)
                    return state.Snapshot;
            })
            .OfType<NearbyScanSnapshot>()
            .OrderBy(snapshot => snapshot.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    /// <summary>Clears retained Nearby state for a released profile by identity.</summary>
    internal void ResetProfile(PlayerProfileId profileId) => ClearProfile(profileId);

    private void ProcessLine(OrderedEventLine orderedEventLine)
    {
        var state = _profileStates.GetOrAdd(
            orderedEventLine.ProfileId,
            static _ => new ProfileNearbyState());
        var text = orderedEventLine.Line.Text;
        NearbyScanSnapshot? changedSnapshot;
        var clearedSnapshot = false;
        var reportScan = false;
        lock (state.Sync)
        {
            if (ProspectingLineParser.TryParseAugment(text, out var augment))
            {
                changedSnapshot = TryUpgradeEligibleSnapshot(state, augment);
            }
            else
            {
                state.EligibleSnapshot = null;
                if (_sessionBoundaryParser.IsLoggingStarted(text)
                    || _sessionBoundaryParser.IsReconnect(text))
                {
                    clearedSnapshot = ClearState(state) is not null;
                    changedSnapshot = null;
                }
                else if (ProspectingLineParser.TryParseNearbyScan(text, out var observation))
                {
                    changedSnapshot = new NearbyScanSnapshot(
                        orderedEventLine.ProfileId,
                        orderedEventLine.ProfileName,
                        observation.RawDescription,
                        observation.Entries.ToArray(),
                        observation.IsOnlyRock);
                    state.Snapshot = changedSnapshot;
                    state.EligibleSnapshot = changedSnapshot;
                    reportScan = true;
                }
                else
                {
                    changedSnapshot = null;
                }
            }
        }

        if (reportScan)
        {
            _diagnostics.ReportProfile(
                RuntimeDiagnosticLevel.Info,
                nameof(NearbyScanProcessor),
                orderedEventLine.ProfileName,
                "NearbyScan",
                $"Entries={changedSnapshot!.Entries.Count}{(changedSnapshot.IsOnlyRock ? " / OnlyRock" : string.Empty)}");
        }

        if (clearedSnapshot)
            OnNearbyScanChanged(orderedEventLine.ProfileId, null);
        else if (changedSnapshot is not null)
            OnNearbyScanChanged(orderedEventLine.ProfileId, changedSnapshot);
    }

    private void ClearProfile(PlayerProfileId profileId)
    {
        if (!_profileStates.TryGetValue(profileId, out var state))
            return;

        NearbyScanSnapshot? clearedSnapshot;
        lock (state.Sync)
            clearedSnapshot = ClearState(state);

        if (clearedSnapshot is not null)
            OnNearbyScanChanged(profileId, null);
    }

    private static NearbyScanSnapshot? TryUpgradeEligibleSnapshot(
        ProfileNearbyState state,
        ProspectingAugment augment)
    {
        var eligibleSnapshot = state.EligibleSnapshot;
        if (eligibleSnapshot is null || eligibleSnapshot.Augments.Contains(augment))
            return null;

        var replacement = eligibleSnapshot with
        {
            Augments = eligibleSnapshot.Augments.Add(augment)
        };
        state.Snapshot = replacement;
        state.EligibleSnapshot = replacement;
        return replacement;
    }

    private static NearbyScanSnapshot? ClearState(ProfileNearbyState state)
    {
        var snapshot = state.Snapshot;
        state.Snapshot = null;
        state.EligibleSnapshot = null;
        return snapshot;
    }

    private void OnNearbyScanChanged(PlayerProfileId profileId, NearbyScanSnapshot? snapshot) =>
        NearbyScanChanged?.Invoke(this, new NearbyScanChangedEventArgs(profileId, snapshot));

    private sealed class ProfileNearbyState
    {
        public object Sync { get; } = new();

        public NearbyScanSnapshot? Snapshot { get; set; }

        public NearbyScanSnapshot? EligibleSnapshot { get; set; }
    }
}
