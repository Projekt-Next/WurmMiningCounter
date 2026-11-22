using System.Collections.ObjectModel;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class MiningSessionCounter
{
    private readonly object _sync = new();
    private readonly Dictionary<PlayerProfileId, ProfileCounts> _profiles = [];

    public event EventHandler<MiningSessionCounterChangedEventArgs>? CountsChanged;

    public void Add(PlayerProfileId profileId, string profileName, MiningEvent miningEvent)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name cannot be empty or whitespace.", nameof(profileName));

        ArgumentNullException.ThrowIfNull(miningEvent);

        lock (_sync)
        {
            if (!_profiles.TryGetValue(profileId, out var profileCounts))
            {
                profileCounts = new ProfileCounts(profileName);
                _profiles.Add(profileId, profileCounts);
            }

            profileCounts.MaterialCounts.TryGetValue(miningEvent.Material, out var currentCount);
            profileCounts.MaterialCounts[miningEvent.Material] = currentCount + 1;
            if (miningEvent.Material.GetYieldRole() == MiningYieldRole.Primary)
                profileCounts.TotalCount++;
        }

        OnCountsChanged(profileId);
    }

    public IReadOnlyDictionary<PlayerProfileId, IReadOnlyDictionary<MiningMaterial, long>> GetSnapshot()
    {
        lock (_sync)
        {
            var profiles = new Dictionary<PlayerProfileId, IReadOnlyDictionary<MiningMaterial, long>>(_profiles.Count);
            foreach (var (profileId, profileCounts) in _profiles)
            {
                var materials = new Dictionary<MiningMaterial, long>(profileCounts.MaterialCounts);
                profiles.Add(profileId, new ReadOnlyDictionary<MiningMaterial, long>(materials));
            }

            return new ReadOnlyDictionary<PlayerProfileId, IReadOnlyDictionary<MiningMaterial, long>>(profiles);
        }
    }

    public IReadOnlyDictionary<PlayerProfileId, ProfileMiningSessionSnapshot> GetProfileSnapshots()
    {
        lock (_sync)
        {
            var profiles = new Dictionary<PlayerProfileId, ProfileMiningSessionSnapshot>(_profiles.Count);
            foreach (var (profileId, profileCounts) in _profiles)
            {
                var materials = new ReadOnlyDictionary<MiningMaterial, long>(
                    new Dictionary<MiningMaterial, long>(profileCounts.MaterialCounts));
                profiles.Add(profileId, new ProfileMiningSessionSnapshot(profileId, profileCounts.ProfileName, profileCounts.TotalCount, materials));
            }

            return new ReadOnlyDictionary<PlayerProfileId, ProfileMiningSessionSnapshot>(profiles);
        }
    }

    public void ResetProfileMaterialCounters(PlayerProfileId profileId)
    {
        ResetMaterialsCore(profileId, materials: null);
    }

    public void ResetMaterials(PlayerProfileId profileId, IReadOnlyCollection<MiningMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        ResetMaterialsCore(profileId, materials);
    }

    public void Reset()
    {
        var changed = false;
        lock (_sync)
        {
            changed = _profiles.Count > 0;
            _profiles.Clear();
        }

        if (changed)
            OnCountsChanged(null);
    }

    /// <summary>Removes the complete session state for one profile, including TotalCount.</summary>
    internal void ResetProfile(PlayerProfileId profileId)
    {
        var changed = false;
        lock (_sync)
            changed = _profiles.Remove(profileId);

        if (changed)
            OnCountsChanged(profileId);
    }

    private void OnCountsChanged(PlayerProfileId? profileId) =>
        CountsChanged?.Invoke(this, new MiningSessionCounterChangedEventArgs(profileId));

    private void ResetMaterialsCore(
        PlayerProfileId profileId,
        IReadOnlyCollection<MiningMaterial>? materials)
    {
        var changed = false;
        lock (_sync)
        {
            if (!_profiles.TryGetValue(profileId, out var profileCounts))
                return;

            if (materials is null)
            {
                changed = profileCounts.MaterialCounts.Count > 0;
                profileCounts.MaterialCounts.Clear();
            }
            else
            {
                foreach (var material in materials)
                    changed |= profileCounts.MaterialCounts.Remove(material);
            }
        }

        if (changed)
            OnCountsChanged(profileId);
    }

    private sealed class ProfileCounts
    {
        public ProfileCounts(string profileName) => ProfileName = profileName;
        public string ProfileName { get; }
        public Dictionary<MiningMaterial, long> MaterialCounts { get; } = new();

        public long TotalCount { get; set; }
    }
}
