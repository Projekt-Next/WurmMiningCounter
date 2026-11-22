using WurmMiningCounter.Models;

namespace WurmMiningCounter.MiningStatistics;

public sealed record MiningStatisticsSessionBaselines(
    long MiningTickBaseline,
    decimal MiningGainBaseline,
    DateTimeOffset? SessionStartedAtUtc);

public sealed class MiningStatisticsSessionBaselineChangedEventArgs(PlayerProfileId profileId) : EventArgs
{
    public PlayerProfileId ProfileId { get; } = profileId;
}

/// <summary>Application-lifetime, in-memory baselines for current Mining statistics.</summary>
public sealed class MiningStatisticsSessionBaselineStore
{
    private readonly object _sync = new();
    private readonly Dictionary<PlayerProfileId, MiningStatisticsSessionBaselines> _profiles = [];

    public event EventHandler<MiningStatisticsSessionBaselineChangedEventArgs>? BaselineChanged;

    public MiningStatisticsSessionBaselines Get(PlayerProfileId profileId)
    {
        lock (_sync)
            return _profiles.TryGetValue(profileId, out var baselines)
                ? baselines
                : new MiningStatisticsSessionBaselines(0, 0m, null);
    }

    public void CaptureMiningSessionBaseline(
        PlayerProfileId profileId,
        long rawMiningTickCount,
        decimal rawMiningGain,
        DateTimeOffset sessionStartedAtUtc)
    {
        if (rawMiningTickCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rawMiningTickCount));
        if (rawMiningGain < 0m)
            throw new ArgumentOutOfRangeException(nameof(rawMiningGain));

        var changed = false;
        lock (_sync)
        {
            var baselines = new MiningStatisticsSessionBaselines(
                rawMiningTickCount,
                rawMiningGain,
                sessionStartedAtUtc);
            if (_profiles.TryGetValue(profileId, out var existing) && existing == baselines)
                return;

            _profiles[profileId] = baselines;
            changed = true;
        }

        if (changed)
            BaselineChanged?.Invoke(this, new MiningStatisticsSessionBaselineChangedEventArgs(profileId));
    }
}
