using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class MiningInspirationTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<PlayerProfileId, ProfileState> _profiles = [];
    private readonly MiningActionContextParser _actionContextParser = new();
    private readonly SessionBoundaryParser _sessionBoundaryParser = new();
    private readonly MiningInspirationCounter _counter;

    public MiningInspirationTracker(MiningInspirationCounter counter)
    {
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
    }

    public void Process(EventLogLine line, MiningEvent? miningEvent)
    {
        ArgumentNullException.ThrowIfNull(line);

        lock (_sync)
        {
            if (_sessionBoundaryParser.IsLoggingStarted(line.Text)
                || _sessionBoundaryParser.IsReconnect(line.Text))
            {
                _profiles.Remove(line.ProfileId);
                return;
            }

            var state = GetOrCreateState(line.ProfileId);
            if (_actionContextParser.Parse(line.Text) is not null)
            {
                state.StartGeneration();
                return;
            }

            if (MiningInspirationParser.IsMomentOfInspiration(line.Text))
            {
                if (state.IsMiningActive && state.PendingMiningMoi is null)
                    state.PendingMiningMoi = new PendingMiningMoi(state.ActiveMiningGeneration);

                return;
            }

            switch (MiningAttemptTransitionParser.Parse(line.Text))
            {
                case MiningAttemptTransitionKind.WallBreakSoon:
                case MiningAttemptTransitionKind.InformationalWarning:
                    // A confirmed warning belongs to the active mining action.
                    // Keep pending MOI for the final qualifying primary yield.
                    return;

                case MiningAttemptTransitionKind.NoYieldTerminal:
                case MiningAttemptTransitionKind.ContextSensitiveNoYield:
                    state.DeactivateMining();
                    return;

                case MiningAttemptTransitionKind.PreStartRejection:
                    // This rejects a new start but does not prove an earlier action ended.
                    state.ClearPendingMoi();
                    return;
            }

            if (state.PendingMiningMoi is { } pendingMoi)
            {
                if (pendingMoi.Generation == state.ActiveMiningGeneration
                    && miningEvent is { Material: var material }
                    && material.IsPrimaryForCounter())
                {
                    _counter.Add(line.ProfileId, line.ProfileName, material);
                    state.ClearPendingMoi();
                    state.DeactivateMining();
                    return;
                }

                // A non-primary line between MOI and a yield makes correlation unsafe.
                state.ClearPendingMoi();
                return;
            }

            if (miningEvent is { Material: var primaryMaterial } && primaryMaterial.IsPrimaryForCounter())
            {
                state.DeactivateMining();
            }
        }
    }

    public void Reset()
    {
        lock (_sync) _profiles.Clear();
        _counter.Reset();
    }

    public void ClearProfilePending(PlayerProfileId profileId)
    {
        lock (_sync) _profiles.Remove(profileId);
    }

    /// <summary>Resets both pending correlation state and counted inspirations for one profile.</summary>
    internal void ResetProfile(PlayerProfileId profileId)
    {
        lock (_sync) _profiles.Remove(profileId);
        _counter.ResetProfile(profileId);
    }

    /// <summary>
    /// Resets counted inspirations and any pre-reset pending correlation for one profile,
    /// while preserving the active Mining action needed to recognize a later MOI.
    /// </summary>
    internal void ResetProfileInspiration(PlayerProfileId profileId)
    {
        lock (_sync)
        {
            if (_profiles.TryGetValue(profileId, out var state))
                state.ClearPendingMoi();
        }

        _counter.ResetProfile(profileId);
    }

    private ProfileState GetOrCreateState(PlayerProfileId profileId)
    {
        if (!_profiles.TryGetValue(profileId, out var state))
        {
            state = new ProfileState();
            _profiles.Add(profileId, state);
        }

        return state;
    }

    private sealed class ProfileState
    {
        public long ActiveMiningGeneration { get; private set; }
        public bool IsMiningActive { get; private set; }
        public PendingMiningMoi? PendingMiningMoi { get; set; }

        public void StartGeneration()
        {
            ActiveMiningGeneration++;
            IsMiningActive = true;
            ClearPendingMoi();
        }

        public void DeactivateMining()
        {
            IsMiningActive = false;
            ClearPendingMoi();
        }

        public void ClearPendingMoi() => PendingMiningMoi = null;
    }

    private sealed record PendingMiningMoi(long Generation);
}
