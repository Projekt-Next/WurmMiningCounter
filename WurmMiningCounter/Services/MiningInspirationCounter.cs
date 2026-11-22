using System.Collections.ObjectModel;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class MiningInspirationCounter
{
    private readonly object _sync = new();
    private readonly Dictionary<PlayerProfileId, ProfileCounts> _profiles = [];

    public void Add(PlayerProfileId profileId, string profileName, MiningMaterial primaryYield)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        if (!primaryYield.IsPrimaryForCounter())
            throw new ArgumentOutOfRangeException(nameof(primaryYield));

        lock (_sync)
        {
            if (!_profiles.TryGetValue(profileId, out var counts))
            {
                counts = new ProfileCounts(profileName);
                _profiles.Add(profileId, counts);
            }

            counts.PrimaryYieldCounts.TryGetValue(primaryYield, out var currentCount);
            counts.PrimaryYieldCounts[primaryYield] = currentCount + 1;
            counts.TotalCount++;
        }
    }

    public IReadOnlyDictionary<PlayerProfileId, MiningInspirationSnapshot> GetProfileSnapshots()
    {
        lock (_sync)
        {
            var snapshots = new Dictionary<PlayerProfileId, MiningInspirationSnapshot>(_profiles.Count);
            foreach (var (profileId, counts) in _profiles)
            {
                snapshots.Add(profileId, new MiningInspirationSnapshot(
                    profileId,
                    counts.ProfileName,
                    counts.TotalCount,
                    new ReadOnlyDictionary<MiningMaterial, long>(
                        new Dictionary<MiningMaterial, long>(counts.PrimaryYieldCounts))));
            }

            return new ReadOnlyDictionary<PlayerProfileId, MiningInspirationSnapshot>(snapshots);
        }
    }

    public void Reset() { lock (_sync) _profiles.Clear(); }

    public void ResetProfile(PlayerProfileId profileId)
    {
        lock (_sync) _profiles.Remove(profileId);
    }

    private sealed class ProfileCounts
    {
        public ProfileCounts(string profileName) => ProfileName = profileName;
        public string ProfileName { get; }
        public Dictionary<MiningMaterial, long> PrimaryYieldCounts { get; } = new();
        public long TotalCount { get; set; }
    }
}
