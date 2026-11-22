using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WurmMiningCounter.Models;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly MiningSessionCounter _counter;
    private readonly MiningInspirationCounter? _miningInspirationCounter;
    private readonly Func<Task> _resetSessionAsync;
    private readonly Func<PlayerProfileId, Task>? _resetVeinContextAsync;
    private readonly Func<PlayerProfileId, Task>? _resetMiningInspirationAsync;
    private readonly Action<PlayerProfileId>? _clearMiningInspirationPending;
    private readonly ObservableCollection<ProfileVeinContextViewModel> _veinContexts;
    private readonly ObservableCollection<ProfilePanelViewModel> _profilePanels;
    private readonly VeinContextProcessor? _veinContextProcessor;
    private readonly NearbyScanProcessor? _nearbyScanProcessor;
    private readonly AnalyseProcessor? _analyseProcessor;
    private readonly Action<Action>? _postToUi;
    private readonly LiveSkillMonitor? _liveSkillMonitor;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Dictionary<PlayerProfileId, LocalExactDeltaState> _localExactDeltas = [];
    private IReadOnlyDictionary<PlayerProfileId, long> _miningInspirationTotals =
        new Dictionary<PlayerProfileId, long>();
    private readonly Dictionary<PlayerProfileId, NearbyScanSnapshot> _nearbyScans = [];
    private readonly Dictionary<PlayerProfileId, ProspectingResult.PlainRock> _plainRockProspectingResults = [];
    private readonly Dictionary<PlayerProfileId, LiveSkillProfileSnapshot> _liveSkillByProfile = [];
    private readonly Dictionary<PlayerProfileId, string> _profileNames = [];
    private int _veinContextSubscriptionVersion;
    private int _isVeinContextSubscriptionAttached;
    private int _nearbyScanSubscriptionVersion;
    private int _isNearbyScanSubscriptionAttached;
    private int _liveSkillSubscriptionVersion;
    private int _isLiveSkillSubscriptionAttached;
    private int _isAnalyseSubscriptionAttached;
    private string _statusText;

    public MainViewModel(
        MiningSessionCounter counter,
        Func<Task>? resetSessionAsync = null,
        IEnumerable<VeinContextSnapshot>? initialVeinContexts = null,
        VeinContextProcessor? veinContextProcessor = null,
        Action<Action>? postToUi = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<PlayerProfileId, Task>? resetVeinContextAsync = null,
        Func<PlayerProfileId, Task>? resetMiningInspirationAsync = null,
        Action<PlayerProfileId>? clearMiningInspirationPending = null,
        MiningInspirationCounter? miningInspirationCounter = null,
        IEnumerable<NearbyScanSnapshot>? initialNearbyScans = null,
        NearbyScanProcessor? nearbyScanProcessor = null,
        LiveSkillMonitor? liveSkillMonitor = null,
        AnalyseProcessor? analyseProcessor = null)
    {
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _miningInspirationCounter = miningInspirationCounter;
        _resetSessionAsync = resetSessionAsync ?? ResetCounterAsync;
        _resetVeinContextAsync = resetVeinContextAsync;
        _resetMiningInspirationAsync = resetMiningInspirationAsync;
        _clearMiningInspirationPending = clearMiningInspirationPending;
        _veinContextProcessor = veinContextProcessor;
        _nearbyScanProcessor = nearbyScanProcessor;
        _analyseProcessor = analyseProcessor;
        _postToUi = postToUi;
        _liveSkillMonitor = liveSkillMonitor;
        _delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        if ((_veinContextProcessor is not null || _nearbyScanProcessor is not null || _liveSkillMonitor is not null || _analyseProcessor is not null)
            && _postToUi is null)
            throw new ArgumentNullException(nameof(postToUi));

        _statusText = "Monitoring unavailable";
        Profiles = new ObservableCollection<ProfileMiningSummaryViewModel>();
        _veinContexts = new ObservableCollection<ProfileVeinContextViewModel>();
        VeinContexts = new ReadOnlyObservableCollection<ProfileVeinContextViewModel>(_veinContexts);
        _profilePanels = new ObservableCollection<ProfilePanelViewModel>();
        ProfilePanels = new ReadOnlyObservableCollection<ProfilePanelViewModel>(_profilePanels);
        LoadVeinContexts(initialVeinContexts ?? _veinContextProcessor?.GetActiveContextSnapshots() ?? []);
        LoadNearbyScans(initialNearbyScans ?? _nearbyScanProcessor?.GetSnapshots() ?? []);
        _counter.CountsChanged += OnMiningCountsChanged;
        AttachVeinContextUpdates();
        AttachNearbyScanUpdates();
        AttachLiveSkillUpdates();
        if (_analyseProcessor is not null)
        {
            _isAnalyseSubscriptionAttached = 1;
            _analyseProcessor.AnalyseChanged += OnAnalyseChanged;
        }
        RebuildProfilePanels();
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProfileMiningSummaryViewModel> Profiles { get; }

    public ReadOnlyObservableCollection<ProfileVeinContextViewModel> VeinContexts { get; }

    public ReadOnlyObservableCollection<ProfilePanelViewModel> ProfilePanels { get; }

    internal AnalyseProcessor? AnalyseProcessor => _analyseProcessor;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
                return;

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyDictionary<PlayerProfileId, IReadOnlyDictionary<MiningMaterial, long>> GetSnapshot()
    {
        return _counter.GetSnapshot();
    }

    public void Refresh()
    {
        var snapshot = _counter.GetProfileSnapshots();
        var profiles = snapshot
            .Where(profile => profile.Value.TotalCount > 0
                || profile.Value.Materials.Values.Any(count => count > 0))
            .OrderBy(profile => profile.Value.ProfileName, StringComparer.OrdinalIgnoreCase)
            .Select(profile => CreateMiningSummary(profile.Key, profile.Value))
            .ToArray();

        var profilesChanged = !HaveSameMiningProfiles(profiles);
        if (profilesChanged)
        {
            Profiles.Clear();
            foreach (var profile in profiles)
                Profiles.Add(profile);
        }

        var inspirationsChanged = RefreshMiningInspirationTotals();
        if (profilesChanged || inspirationsChanged)
            RebuildProfilePanels();
    }

    public void LoadVeinContexts(IEnumerable<VeinContextSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        _veinContexts.Clear();
        foreach (var snapshot in snapshots)
        {
            _profileNames[snapshot.ProfileId] = snapshot.ProfileName;
            ApplyVeinContextSnapshot(snapshot.ProfileId, snapshot);
        }
    }

    public void LoadNearbyScans(IEnumerable<NearbyScanSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        _nearbyScans.Clear();
        foreach (var snapshot in snapshots)
        {
            _profileNames[snapshot.ProfileId] = snapshot.ProfileName;
            _nearbyScans[snapshot.ProfileId] = snapshot;
        }
    }

    public void ApplyNearbyScanSnapshot(PlayerProfileId profileId, NearbyScanSnapshot? snapshot)
    {
        if (snapshot is null)
            _nearbyScans.Remove(profileId);
        else
        {
            _profileNames[profileId] = snapshot.ProfileName;
            _nearbyScans[profileId] = snapshot;
        }

        RebuildProfilePanels();
    }

    public void ApplyVeinContextSnapshot(
        PlayerProfileId profileId,
        VeinContextSnapshot? snapshot,
        bool isLocalExactDecrement = false)
    {
        var existingIndex = FindVeinContextIndex(profileId);
        if (snapshot is null)
        {
            ClearLocalExactDelta(profileId);
            if (existingIndex >= 0)
                _veinContexts.RemoveAt(existingIndex);

            RebuildProfilePanels();

            return;
        }

        _profileNames[profileId] = snapshot.ProfileName;

        var localExactRemainingDelta = GetLocalExactRemainingDelta(
            profileId,
            existingIndex >= 0 ? _veinContexts[existingIndex] : null,
            snapshot,
            isLocalExactDecrement);

        if (existingIndex >= 0)
        {
            _veinContexts[existingIndex] = new ProfileVeinContextViewModel(snapshot, localExactRemainingDelta);
            RebuildProfilePanels();
            return;
        }

        _veinContexts.Insert(
            FindVeinContextInsertionIndex(snapshot.ProfileName),
            new ProfileVeinContextViewModel(snapshot, localExactRemainingDelta));
        RebuildProfilePanels();
    }

    public void DetachVeinContextUpdates()
    {
        if (_veinContextProcessor is null
            || Interlocked.Exchange(ref _isVeinContextSubscriptionAttached, 0) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _veinContextSubscriptionVersion);
        _veinContextProcessor.ActiveContextChanged -= OnActiveContextChanged;
        _veinContextProcessor.PlainRockProspectingResultChanged -= OnPlainRockProspectingResultChanged;
    }

    public void DetachLiveSkillUpdates()
    {
        if (_liveSkillMonitor is null
            || Interlocked.Exchange(ref _isLiveSkillSubscriptionAttached, 0) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _liveSkillSubscriptionVersion);
        _liveSkillMonitor.ProfileChanged -= OnLiveSkillProfileChanged;
    }

    public void DetachAnalyseUpdates()
    {
        if (_analyseProcessor is null || Interlocked.Exchange(ref _isAnalyseSubscriptionAttached, 0) == 0)
            return;
        _analyseProcessor.AnalyseChanged -= OnAnalyseChanged;
    }

    private void OnAnalyseChanged(object? sender, AnalyseChangedEventArgs eventArgs)
    {
        _postToUi!(() =>
        {
            if (Volatile.Read(ref _isAnalyseSubscriptionAttached) != 0)
                RebuildProfilePanels();
        });
    }

    public async Task ResetSessionAsync()
    {
        await _resetSessionAsync();
        Refresh();
    }

    public void ResetProfileMaterialCounters(PlayerProfileId profileId)
    {
        _counter.ResetProfileMaterialCounters(profileId);
        _clearMiningInspirationPending?.Invoke(profileId);
        Refresh();
    }

    public void ResetMaterials(PlayerProfileId profileId, IReadOnlyCollection<MiningMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);

        _counter.ResetMaterials(profileId, materials);
        Refresh();
    }

    public async Task ResetVeinContextAsync(PlayerProfileId profileId)
    {
        if (_resetVeinContextAsync is null)
        {
            throw new InvalidOperationException(
                "Per-profile Vein Context reset is not configured.");
        }

        await _resetVeinContextAsync(profileId);
    }

    public async Task ResetMiningInspirationAsync(PlayerProfileId profileId)
    {
        if (_resetMiningInspirationAsync is null)
        {
            throw new InvalidOperationException(
                "Per-profile Mining Inspiration reset is not configured.");
        }

        await _resetMiningInspirationAsync(profileId);
        RefreshMiningProfile(profileId);
    }

    public void SetStatus(string statusText)
    {
        ArgumentNullException.ThrowIfNull(statusText);
        StatusText = statusText;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private Task ResetCounterAsync()
    {
        _counter.Reset();
        return Task.CompletedTask;
    }

    private void AttachVeinContextUpdates()
    {
        if (_veinContextProcessor is null)
            return;

        Interlocked.Increment(ref _veinContextSubscriptionVersion);
        Interlocked.Exchange(ref _isVeinContextSubscriptionAttached, 1);
        _veinContextProcessor.ActiveContextChanged += OnActiveContextChanged;
        _veinContextProcessor.PlainRockProspectingResultChanged += OnPlainRockProspectingResultChanged;
    }

    private void AttachNearbyScanUpdates()
    {
        if (_nearbyScanProcessor is null)
            return;

        Interlocked.Increment(ref _nearbyScanSubscriptionVersion);
        Interlocked.Exchange(ref _isNearbyScanSubscriptionAttached, 1);
        _nearbyScanProcessor.NearbyScanChanged += OnNearbyScanChanged;
    }

    private void AttachLiveSkillUpdates()
    {
        if (_liveSkillMonitor is null
            || Interlocked.Exchange(ref _isLiveSkillSubscriptionAttached, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _liveSkillSubscriptionVersion);
        _liveSkillMonitor.ProfileChanged += OnLiveSkillProfileChanged;
        LoadLiveSkillSnapshots(_liveSkillMonitor.GetSnapshots());
    }

    private void OnLiveSkillProfileChanged(object? sender, LiveSkillProfileChangedEventArgs eventArgs)
    {
        var subscriptionVersion = Volatile.Read(ref _liveSkillSubscriptionVersion);
        _postToUi!(() =>
        {
            if (Volatile.Read(ref _isLiveSkillSubscriptionAttached) == 0
                || subscriptionVersion != Volatile.Read(ref _liveSkillSubscriptionVersion))
            {
                return;
            }

            RefreshLiveSkillProfile(eventArgs.ProfileId);
        });
    }

    private void OnActiveContextChanged(object? sender, VeinContextChangedEventArgs eventArgs)
    {
        var subscriptionVersion = Volatile.Read(ref _veinContextSubscriptionVersion);
        _postToUi!(() =>
        {
            if (Volatile.Read(ref _isVeinContextSubscriptionAttached) == 0
                || subscriptionVersion != Volatile.Read(ref _veinContextSubscriptionVersion))
            {
                return;
            }

            ApplyVeinContextSnapshot(
                eventArgs.ProfileId,
                eventArgs.Snapshot,
                eventArgs.IsLocalExactDecrement);
        });
    }

    private void OnNearbyScanChanged(object? sender, NearbyScanChangedEventArgs eventArgs)
    {
        var subscriptionVersion = Volatile.Read(ref _nearbyScanSubscriptionVersion);
        _postToUi!(() =>
        {
            if (Volatile.Read(ref _isNearbyScanSubscriptionAttached) == 0
                || subscriptionVersion != Volatile.Read(ref _nearbyScanSubscriptionVersion))
            {
                return;
            }

            ApplyNearbyScanSnapshot(eventArgs.ProfileId, eventArgs.Snapshot);
        });
    }

    private void OnPlainRockProspectingResultChanged(
        object? sender,
        PlainRockProspectingResultChangedEventArgs eventArgs)
    {
        var subscriptionVersion = Volatile.Read(ref _veinContextSubscriptionVersion);
        _postToUi!(() =>
        {
            if (Volatile.Read(ref _isVeinContextSubscriptionAttached) == 0
                || subscriptionVersion != Volatile.Read(ref _veinContextSubscriptionVersion))
            {
                return;
            }

            ApplyPlainRockProspectingResult(eventArgs.ProfileId, eventArgs.ProfileName, eventArgs.Result);
        });
    }

    private void ApplyPlainRockProspectingResult(
        PlayerProfileId profileId,
        string profileName,
        ProspectingResult.PlainRock? result)
    {
        _profileNames[profileId] = profileName;
        if (result is null)
            _plainRockProspectingResults.Remove(profileId);
        else
            _plainRockProspectingResults[profileId] = result;

        RebuildProfilePanels();
    }

    private void OnMiningCountsChanged(object? sender, MiningSessionCounterChangedEventArgs eventArgs)
    {
        PostToUi(() =>
        {
            if (eventArgs.ProfileId is null)
                Refresh();
            else
                RefreshMiningProfile(eventArgs.ProfileId.Value);
        });
    }

    private void RefreshMiningProfile(PlayerProfileId profileId)
    {
        var snapshot = _counter.GetProfileSnapshots();
        snapshot.TryGetValue(profileId, out var profileSnapshot);
        var candidate = profileSnapshot is not null
            && (profileSnapshot.TotalCount > 0 || profileSnapshot.Materials.Values.Any(count => count > 0))
            ? CreateMiningSummary(profileId, profileSnapshot)
            : null;
        var existingIndex = FindMiningProfileIndex(profileId);
        var profilesChanged = false;

        if (candidate is null)
        {
            if (existingIndex >= 0)
            {
                Profiles.RemoveAt(existingIndex);
                profilesChanged = true;
            }
        }
        else if (existingIndex < 0)
        {
            Profiles.Insert(FindMiningProfileInsertionIndex(candidate.ProfileName), candidate);
            profilesChanged = true;
        }
        else if (!HaveSameMiningProfile(Profiles[existingIndex], candidate))
        {
            Profiles[existingIndex] = candidate;
            profilesChanged = true;
        }

        var inspirationsChanged = RefreshMiningInspirationTotals();
        if (profilesChanged || inspirationsChanged)
            RebuildProfilePanels();
    }

    private bool RefreshMiningInspirationTotals()
    {
        var totals = _miningInspirationCounter?.GetProfileSnapshots()
            .Where(profile => profile.Value.TotalCount > 0)
            .ToDictionary(profile => profile.Key, profile => profile.Value.TotalCount)
            ?? new Dictionary<PlayerProfileId, long>();
        if (_miningInspirationTotals.Count == totals.Count
            && _miningInspirationTotals.All(pair =>
                totals.TryGetValue(pair.Key, out var value) && value == pair.Value))
        {
            return false;
        }

        _miningInspirationTotals = totals;
        return true;
    }

    private void LoadLiveSkillSnapshots(IReadOnlyList<LiveSkillProfileSnapshot> snapshots)
    {
        _liveSkillByProfile.Clear();
        foreach (var snapshot in snapshots)
        {
            _profileNames[snapshot.ProfileId] = snapshot.ProfileName;
            _liveSkillByProfile[snapshot.ProfileId] = snapshot;
        }
    }

    private void RefreshLiveSkillProfile(PlayerProfileId profileId)
    {
        var snapshot = _liveSkillMonitor!.GetSnapshots()
            .FirstOrDefault(candidate => candidate.ProfileId == profileId);
        if (snapshot is null)
            _liveSkillByProfile.Remove(profileId);
        else
        {
            _profileNames[profileId] = snapshot.ProfileName;
            _liveSkillByProfile[profileId] = snapshot;
        }

        RebuildProfilePanels();
    }

    private static ProfileMiningSummaryViewModel CreateMiningSummary(
        PlayerProfileId profileId,
        ProfileMiningSessionSnapshot snapshot) =>
        new(
            profileId,
            snapshot.ProfileName,
            snapshot.TotalCount,
            snapshot.Materials
                .Where(material => material.Value > 0)
                .OrderBy(material => material.Key)
                .Select(material => new MiningMaterialCountViewModel(material.Key, material.Value))
                .ToArray());

    private int? GetLocalExactRemainingDelta(
        PlayerProfileId profileId,
        ProfileVeinContextViewModel? previousContext,
        VeinContextSnapshot snapshot,
        bool isLocalExactDecrement)
    {
        if (!isLocalExactDecrement
            || previousContext?.Remaining is not RemainingMeasurement.Exact previousRemaining
            || snapshot.Remaining is not RemainingMeasurement.Exact currentRemaining)
        {
            ClearLocalExactDelta(profileId);
            return null;
        }

        var baseRemaining = _localExactDeltas.TryGetValue(profileId, out var existingDelta)
            ? existingDelta.BaseRemaining
            : previousRemaining.Value;
        var delta = baseRemaining - currentRemaining.Value;
        if (delta <= 0)
        {
            ClearLocalExactDelta(profileId);
            return null;
        }

        ClearLocalExactDelta(profileId);
        var state = new LocalExactDeltaState(baseRemaining, snapshot);
        _localExactDeltas.Add(profileId, state);
        _ = ClearLocalExactDeltaAfterDelayAsync(profileId, state);
        return delta;
    }

    private async Task ClearLocalExactDeltaAfterDelayAsync(PlayerProfileId profileId, LocalExactDeltaState state)
    {
        try
        {
            await _delayAsync(TimeSpan.FromSeconds(1), state.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        PostToUi(() =>
        {
            if (!_localExactDeltas.TryGetValue(profileId, out var currentState)
                || !ReferenceEquals(currentState, state))
            {
                return;
            }

            _localExactDeltas.Remove(profileId);
            var existingIndex = FindVeinContextIndex(profileId);
            if (existingIndex >= 0)
            {
                _veinContexts[existingIndex] = new ProfileVeinContextViewModel(state.Snapshot);
                RebuildProfilePanels();
            }
        });
    }

    private void ClearLocalExactDelta(PlayerProfileId profileId)
    {
        if (_localExactDeltas.Remove(profileId, out var state))
            state.Cancellation.Cancel();
    }

    private void PostToUi(Action action)
    {
        if (_postToUi is null)
            action();
        else
            _postToUi(action);
    }

    private int FindVeinContextIndex(PlayerProfileId profileId)
    {
        for (var index = 0; index < _veinContexts.Count; index++)
        {
            if (_veinContexts[index].ProfileId == profileId)
                return index;
        }

        return -1;
    }

    private int FindMiningProfileIndex(PlayerProfileId profileId)
    {
        for (var index = 0; index < Profiles.Count; index++)
        {
            if (Profiles[index].ProfileId == profileId)
                return index;
        }

        return -1;
    }

    private int FindMiningProfileInsertionIndex(string profileName)
    {
        for (var index = 0; index < Profiles.Count; index++)
        {
            if (StringComparer.OrdinalIgnoreCase.Compare(Profiles[index].ProfileName, profileName) > 0)
                return index;
        }

        return Profiles.Count;
    }

    private void RebuildProfilePanels()
    {
        var miningByProfile = Profiles.ToDictionary(profile => profile.ProfileId);
        var veinByProfile = _veinContexts.ToDictionary(context => context.ProfileId);
        var nearbyByProfile = _nearbyScans;
        var analyseByProfile = _analyseProcessor?.GetSnapshots().ToDictionary(snapshot => snapshot.ProfileId)
            ?? new Dictionary<PlayerProfileId, AnalyseSnapshot>();
        string ProfileName(PlayerProfileId id)
        {
            var name = GetProfileName(id);
            return name.Length > 0 ? name : analyseByProfile.GetValueOrDefault(id)?.ProfileName ?? string.Empty;
        }
        var profileIds = miningByProfile.Keys
            .Concat(veinByProfile.Keys)
            .Concat(_miningInspirationTotals.Keys)
            .Concat(nearbyByProfile.Keys)
            .Concat(_plainRockProspectingResults.Keys)
            .Concat(analyseByProfile.Keys)
            .Distinct()
            .OrderBy(ProfileName, StringComparer.OrdinalIgnoreCase);

        var panels = profileIds
            .Select(profileId =>
            {
                miningByProfile.TryGetValue(profileId, out var miningSummary);
                veinByProfile.TryGetValue(profileId, out var veinContext);
                _miningInspirationTotals.TryGetValue(profileId, out var miningInspirationCount);
                nearbyByProfile.TryGetValue(profileId, out var nearbyScanSnapshot);
                _plainRockProspectingResults.TryGetValue(profileId, out var plainRockProspectingResult);
                _liveSkillByProfile.TryGetValue(profileId, out var liveSkill);
                return new ProfilePanelViewModel(
                    profileId,
                    ProfileName(profileId),
                    miningSummary,
                    veinContext,
                    miningInspirationCount,
                    nearbyScanSnapshot is null ? null : new ProfileNearbyScanViewModel(nearbyScanSnapshot),
                    plainRockProspectingResult is null
                        ? null
                        : new ProfilePlainRockProspectingResultViewModel(plainRockProspectingResult),
                    liveSkill,
                    analyseByProfile.ContainsKey(profileId));
            })
            .ToArray();

        if (HasSameProfilePanels(panels))
            return;

        _profilePanels.Clear();
        foreach (var panel in panels)
        {
            _profilePanels.Add(panel);
        }
    }

    private bool HasSameProfilePanels(IReadOnlyList<ProfilePanelViewModel> panels)
    {
        if (_profilePanels.Count != panels.Count)
            return false;

        for (var index = 0; index < panels.Count; index++)
        {
            var current = _profilePanels[index];
            var candidate = panels[index];
            if (current.ProfileId != candidate.ProfileId
                || !string.Equals(current.ProfileName, candidate.ProfileName, StringComparison.Ordinal)
                || current.TotalCount != candidate.TotalCount
                || current.MiningInspirationCount != candidate.MiningInspirationCount
                || current.HasAnalyseResult != candidate.HasAnalyseResult
                || !HaveSameMaterials(current.Materials, candidate.Materials)
                || !Equals(current.VeinContext, candidate.VeinContext)
                || !Equals(current.PlainRockProspectingResult?.Quality, candidate.PlainRockProspectingResult?.Quality)
                || !Equals(current.LiveSkill, candidate.LiveSkill)
                || !HaveSameNearbyScan(current.NearbyScan, candidate.NearbyScan))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HaveSameNearbyScan(
        ProfileNearbyScanViewModel? first,
        ProfileNearbyScanViewModel? second)
    {
        if (first is null || second is null)
            return first is null && second is null;

        if (first.IsOnlyRock != second.IsOnlyRock
            || !string.Equals(first.RawDescription, second.RawDescription, StringComparison.Ordinal)
            || first.Entries.Count != second.Entries.Count)
        {
            return false;
        }

        return first.Entries.Zip(second.Entries).All(pair =>
            string.Equals(pair.First.MaterialText, pair.Second.MaterialText, StringComparison.Ordinal)
            && string.Equals(pair.First.GradeText, pair.Second.GradeText, StringComparison.Ordinal));
    }

    private static bool HaveSameMaterials(
        IReadOnlyList<MiningMaterialCountViewModel> first,
        IReadOnlyList<MiningMaterialCountViewModel> second)
    {
        if (first.Count != second.Count)
            return false;

        for (var index = 0; index < first.Count; index++)
        {
            if (first[index].Material != second[index].Material
                || first[index].Count != second[index].Count)
            {
                return false;
            }
        }

        return true;
    }

    private bool HaveSameMiningProfiles(IReadOnlyList<ProfileMiningSummaryViewModel> profiles)
    {
        if (Profiles.Count != profiles.Count)
            return false;

        for (var index = 0; index < profiles.Count; index++)
        {
            if (!HaveSameMiningProfile(Profiles[index], profiles[index]))
                return false;
        }

        return true;
    }

    private static bool HaveSameMiningProfile(
        ProfileMiningSummaryViewModel first,
        ProfileMiningSummaryViewModel second) =>
        first.ProfileId == second.ProfileId
        && string.Equals(first.ProfileName, second.ProfileName, StringComparison.Ordinal)
        && first.TotalCount == second.TotalCount
        && HaveSameMaterials(first.Materials, second.Materials);

    private int FindVeinContextInsertionIndex(string profileName)
    {
        for (var index = 0; index < _veinContexts.Count; index++)
        {
            if (StringComparer.OrdinalIgnoreCase.Compare(_veinContexts[index].ProfileName, profileName) > 0)
                return index;
        }

        return _veinContexts.Count;
    }

    private string GetProfileName(PlayerProfileId profileId)
    {
        if (_profileNames.TryGetValue(profileId, out var profileName)) return profileName;
        if (_liveSkillByProfile.TryGetValue(profileId, out var liveSkill)) return liveSkill.ProfileName;
        if (_nearbyScans.TryGetValue(profileId, out var nearby)) return nearby.ProfileName;
        var mining = Profiles.FirstOrDefault(profile => profile.ProfileId == profileId);
        if (mining is not null) return mining.ProfileName;
        var vein = _veinContexts.FirstOrDefault(context => context.ProfileId == profileId);
        return vein?.ProfileName ?? string.Empty;
    }

    private sealed class LocalExactDeltaState
    {
        public LocalExactDeltaState(int baseRemaining, VeinContextSnapshot snapshot)
        {
            BaseRemaining = baseRemaining;
            Snapshot = snapshot;
        }

        public int BaseRemaining { get; }

        public VeinContextSnapshot Snapshot { get; }

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
