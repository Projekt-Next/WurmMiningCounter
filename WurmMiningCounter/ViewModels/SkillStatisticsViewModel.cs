using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WurmMiningCounter.Models;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.ViewModels;

public sealed class OtherSkillStatisticsRow(string skillName, decimal currentValue, decimal? lastGain, decimal sessionGain)
{
    public string SkillName { get; } = skillName;
    public decimal CurrentValue { get; } = currentValue;
    public decimal? LastGain { get; } = lastGain;
    public decimal SessionGain { get; } = sessionGain;
}

public sealed record SkillStatisticsProfileGroup(string PhysicalRootPath, string DisplayName);

public sealed class SkillStatisticsProfileOption(LiveSkillProfileSnapshot snapshot)
{
    public PlayerProfileId ProfileId { get; } = snapshot.ProfileId;
    public string ProfileName { get; } = snapshot.ProfileName;
    public SkillStatisticsProfileGroup Group { get; } = new(snapshot.PresentationRootPath ?? snapshot.ActiveSourcePath ?? snapshot.ProfileId.ToString(), snapshot.PresentationGroupName);
    public string DisplayText => $"{ProfileName} - {Group.DisplayName}";
}

public sealed class SkillStatisticsViewModel : INotifyPropertyChanged
{
    private readonly LiveSkillMonitor _monitor;
    private readonly MiningSessionCounter _counter;
    private readonly Action<Action> _postToUi;
    private readonly TimeProvider _timeProvider;
    private readonly MiningStatisticsSessionBaselineStore _sessionBaselines;
    private readonly Dictionary<PlayerProfileId, LiveSkillProfileSnapshot> _snapshots = [];
    private int _subscriptionVersion;
    private int _isSubscriptionAttached;
    private PlayerProfileId? _selectedProfileId;
    private string _selectedActionCountText = "100";
    private MiningSkillStatistics _mining = new(null, null, 0m, 0, 0m, null, null);
    private MiningSessionStatistics _sessionStatistics = new(null, null, null, null);

    public SkillStatisticsViewModel(
        LiveSkillMonitor monitor,
        MiningSessionCounter counter,
        Action<Action> postToUi,
        TimeProvider? timeProvider = null,
        MiningStatisticsSessionBaselineStore? sessionBaselines = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionBaselines = sessionBaselines ?? throw new ArgumentNullException(nameof(sessionBaselines));
        Profiles = new ObservableCollection<SkillStatisticsProfileOption>();
        OtherSkills = new ObservableCollection<OtherSkillStatisticsRow>();
        AttachLiveSkillUpdates();
    }

    // Test and design-time convenience overload. Production composition always supplies the application counter and baseline store.
    public SkillStatisticsViewModel(
        LiveSkillMonitor monitor,
        Action<Action> postToUi,
        TimeProvider? timeProvider = null,
        MiningStatisticsSessionBaselineStore? sessionBaselines = null)
        : this(monitor, new MiningSessionCounter(), postToUi, timeProvider,
            sessionBaselines ?? new MiningStatisticsSessionBaselineStore())
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SkillStatisticsProfileOption> Profiles { get; }
    public ObservableCollection<OtherSkillStatisticsRow> OtherSkills { get; }

    public PlayerProfileId? SelectedProfileId
    {
        get => _selectedProfileId;
        set
        {
            if (_selectedProfileId == value) return;
            _selectedProfileId = value;
            OnPropertyChanged();
            RefreshSelectedProfile();
        }
    }

    public string SelectedActionCountText
    {
        get => _selectedActionCountText;
        set
        {
            if (_selectedActionCountText == value) return;
            _selectedActionCountText = value;
            OnPropertyChanged();
            RefreshSessionStatistics();
        }
    }

    public decimal? MiningCurrent => _mining.CurrentValue;
    public decimal? MiningLastGain => _mining.LastGain;
    public decimal MiningSessionGain => _mining.SessionGain;
    public long SuccessfulActions => _selectedProfileId is { } profileId
        && _counter.GetProfileSnapshots().TryGetValue(profileId, out var snapshot) ? snapshot.TotalCount : 0;
    public long MiningTickCount => MiningCurrent is null ? 0 : _mining.PositiveUpdateCount - GetSelectedBaseline().MiningTickBaseline;
    public decimal SessionMiningGain => MiningCurrent is null ? 0m : _mining.SessionGain - GetSelectedBaseline().MiningGainBaseline;
    public decimal MiningGainPerHour => _mining.GainPerHour;
    public decimal? NextMiningMilestone => _mining.NextMilestone;
    public TimeSpan? MiningTimeToMilestone => _mining.TimeToMilestone;
    public decimal? TickRatePercent => _sessionStatistics.TickRatePercent;
    public decimal? ActionsPerTick => _sessionStatistics.ActionsPerTick;
    public decimal? AverageGainPerTick => _sessionStatistics.AverageGainPerTick;
    public decimal? TicksPerHour => _sessionStatistics.TicksPerHour;
    public decimal? TicksPerSelectedActions => MiningCurrent is null || !TryGetSelectedActionCount(out var selectedActionCount)
        ? null
        : MiningSessionStatisticsCalculator.CalculateTicksPerSelectedActions(SuccessfulActions, MiningTickCount, selectedActionCount);

    public string MiningCurrentDisplay => FormatNullable(MiningCurrent);
    public string MiningLastGainDisplay => FormatNullable(MiningLastGain, true);
    public string MiningSessionGainDisplay => MiningCurrent is null ? "—" : Format(MiningSessionGain, MiningSessionGain > 0m);
    public string SuccessfulActionsDisplay => SuccessfulActions.ToString(CultureInfo.InvariantCulture);
    public string MiningTickCountDisplay => MiningCurrent is null ? "—" : MiningTickCount.ToString(CultureInfo.InvariantCulture);
    public string TickRatePercentDisplay => FormatTwoDecimals(TickRatePercent, "%");
    public string ActionsPerTickDisplay => FormatTwoDecimals(ActionsPerTick);
    public string AverageGainPerTickDisplay => AverageGainPerTick is { } value ? value.ToString("0.######", CultureInfo.InvariantCulture) : "—";
    public string TicksPerHourDisplay => TicksPerHour is { } value
        ? "≈ " + value.ToString("0.00", CultureInfo.InvariantCulture)
        : "—";
    public string TicksPerSelectedActionsDisplay => TicksPerSelectedActions is { } value ? "≈ " + value.ToString("0.00", CultureInfo.InvariantCulture) : "—";
    public string MiningGainPerHourDisplay => MiningCurrent is null ? "—" : Format(MiningGainPerHour, MiningGainPerHour > 0m);
    public string NextMiningMilestoneDisplay => FormatNullable(NextMiningMilestone);
    public string MiningTimeToMilestoneDisplay => MiningTimeToMilestone is { } eta ? FormatDuration(eta) : "—";

    public void RefreshTimeDerivedMetrics()
    {
        if (_selectedProfileId is null || !_snapshots.TryGetValue(_selectedProfileId.Value, out var snapshot))
            SetMining(new MiningSkillStatistics(null, null, 0m, 0, 0m, null, null));
        else
            SetMining(SkillStatisticsCalculator.Calculate(snapshot.Skills.FirstOrDefault(skill => skill.SkillName == "Mining"), snapshot.ObservationStartedAtUtc, _timeProvider.GetUtcNow()));
        RefreshSessionStatistics();
    }

    public void DetachLiveSkillUpdates()
    {
        if (Interlocked.Exchange(ref _isSubscriptionAttached, 0) == 0) return;
        Interlocked.Increment(ref _subscriptionVersion);
        _monitor.ProfileChanged -= OnProfileChanged;
        _sessionBaselines.BaselineChanged -= OnBaselineChanged;
        _counter.CountsChanged -= OnMiningCountsChanged;
    }

    private void AttachLiveSkillUpdates()
    {
        if (Interlocked.Exchange(ref _isSubscriptionAttached, 1) != 0) return;
        Interlocked.Increment(ref _subscriptionVersion);
        _monitor.ProfileChanged += OnProfileChanged;
        _sessionBaselines.BaselineChanged += OnBaselineChanged;
        _counter.CountsChanged += OnMiningCountsChanged;
        ApplySnapshots(_monitor.GetSnapshots());
    }

    private void OnProfileChanged(object? sender, LiveSkillProfileChangedEventArgs eventArgs) => PostIfAttached(() => ApplySnapshots(_monitor.GetSnapshots()));
    private void OnBaselineChanged(object? sender, MiningStatisticsSessionBaselineChangedEventArgs eventArgs) => PostIfAttached(() =>
    {
        if (_selectedProfileId == eventArgs.ProfileId) RefreshTimeDerivedMetrics();
    });
    private void OnMiningCountsChanged(object? sender, MiningSessionCounterChangedEventArgs eventArgs) => PostIfAttached(() =>
    {
        if (eventArgs.ProfileId is null || _selectedProfileId == eventArgs.ProfileId) RefreshSessionStatistics();
    });

    private void PostIfAttached(Action action)
    {
        var version = Volatile.Read(ref _subscriptionVersion);
        _postToUi(() =>
        {
            if (Volatile.Read(ref _isSubscriptionAttached) == 0 || version != Volatile.Read(ref _subscriptionVersion)) return;
            action();
        });
    }

    private void ApplySnapshots(IReadOnlyList<LiveSkillProfileSnapshot> snapshots)
    {
        _snapshots.Clear();
        foreach (var snapshot in snapshots) _snapshots[snapshot.ProfileId] = snapshot;
        var orderedProfiles = _snapshots.Values
            .OrderBy(profile => profile.PresentationGroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.PresentationRootPath ?? profile.ActiveSourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        UpdateProfiles(orderedProfiles);
        if (_selectedProfileId is null || !_snapshots.ContainsKey(_selectedProfileId.Value))
        {
            _selectedProfileId = orderedProfiles.FirstOrDefault()?.ProfileId;
            OnPropertyChanged(nameof(SelectedProfileId));
        }
        RefreshSelectedProfile();
    }

    // Do not clear the collection: Selector clears its selection during a Reset and writes that transient null through TwoWay binding.
    // Updating and moving items by stable profile ID preserves a still-existing selection while keeping the current sort order.
    private void UpdateProfiles(IReadOnlyList<LiveSkillProfileSnapshot> orderedProfiles)
    {
        for (var index = Profiles.Count - 1; index >= 0; index--)
            if (!_snapshots.ContainsKey(Profiles[index].ProfileId))
                Profiles.RemoveAt(index);

        for (var targetIndex = 0; targetIndex < orderedProfiles.Count; targetIndex++)
        {
            var target = orderedProfiles[targetIndex];
            var existingIndex = -1;
            for (var index = targetIndex; index < Profiles.Count; index++)
            {
                if (Profiles[index].ProfileId == target.ProfileId)
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex < 0)
                Profiles.Insert(targetIndex, new SkillStatisticsProfileOption(target));
            else
            {
                if (existingIndex != targetIndex)
                    Profiles.Move(existingIndex, targetIndex);
                // Replacing Selector's currently selected item also causes it to momentarily clear SelectedValue.
                // Its display identity is stable; the current snapshot remains in _snapshots for all statistics.
                if (Profiles[targetIndex].ProfileId != _selectedProfileId)
                    Profiles[targetIndex] = new SkillStatisticsProfileOption(target);
            }
        }
    }

    private void RefreshSelectedProfile()
    {
        OtherSkills.Clear();
        if (_selectedProfileId is not null && _snapshots.TryGetValue(_selectedProfileId.Value, out var snapshot))
            foreach (var skill in snapshot.Skills.Where(skill => skill.SkillName != "Mining").OrderBy(skill => skill.SkillName, StringComparer.Ordinal))
                OtherSkills.Add(new OtherSkillStatisticsRow(skill.SkillName, skill.CurrentValue, skill.LastGain, skill.SessionGain));
        RefreshTimeDerivedMetrics();
        OnPropertyChanged(nameof(HasOtherSkills));
    }

    public bool HasOtherSkills => OtherSkills.Count > 0;

    private void RefreshSessionStatistics()
    {
        if (MiningCurrent is null || _selectedProfileId is null || !_snapshots.TryGetValue(_selectedProfileId.Value, out var snapshot))
            _sessionStatistics = new MiningSessionStatistics(null, null, null, null);
        else
        {
            var baseline = GetSelectedBaseline();
            _sessionStatistics = MiningSessionStatisticsCalculator.Calculate(SuccessfulActions, MiningTickCount, SessionMiningGain,
                _timeProvider.GetUtcNow() - (baseline.SessionStartedAtUtc ?? snapshot.ObservationStartedAtUtc));
        }
        OnPropertyChanged(nameof(SuccessfulActions)); OnPropertyChanged(nameof(SuccessfulActionsDisplay));
        OnPropertyChanged(nameof(MiningTickCount)); OnPropertyChanged(nameof(MiningTickCountDisplay));
        OnPropertyChanged(nameof(SessionMiningGain));
        OnPropertyChanged(nameof(TickRatePercent)); OnPropertyChanged(nameof(TickRatePercentDisplay));
        OnPropertyChanged(nameof(ActionsPerTick)); OnPropertyChanged(nameof(ActionsPerTickDisplay));
        OnPropertyChanged(nameof(AverageGainPerTick)); OnPropertyChanged(nameof(AverageGainPerTickDisplay));
        OnPropertyChanged(nameof(TicksPerHour)); OnPropertyChanged(nameof(TicksPerHourDisplay));
        OnPropertyChanged(nameof(TicksPerSelectedActions)); OnPropertyChanged(nameof(TicksPerSelectedActionsDisplay));
    }

    private MiningStatisticsSessionBaselines GetSelectedBaseline() => _selectedProfileId is { } profileId
        ? _sessionBaselines.Get(profileId) : new MiningStatisticsSessionBaselines(0, 0m, null);
    private bool TryGetSelectedActionCount(out long value) => long.TryParse(_selectedActionCountText, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private void SetMining(MiningSkillStatistics value)
    {
        if (_mining == value) return;
        _mining = value;
        OnPropertyChanged(nameof(MiningCurrent)); OnPropertyChanged(nameof(MiningLastGain)); OnPropertyChanged(nameof(MiningSessionGain));
        OnPropertyChanged(nameof(MiningGainPerHour)); OnPropertyChanged(nameof(NextMiningMilestone)); OnPropertyChanged(nameof(MiningTimeToMilestone));
        OnPropertyChanged(nameof(MiningCurrentDisplay)); OnPropertyChanged(nameof(MiningLastGainDisplay)); OnPropertyChanged(nameof(MiningSessionGainDisplay));
        OnPropertyChanged(nameof(MiningGainPerHourDisplay)); OnPropertyChanged(nameof(NextMiningMilestoneDisplay)); OnPropertyChanged(nameof(MiningTimeToMilestoneDisplay));
    }

    private static string FormatNullable(decimal? value, bool includePlus = false) => value is { } number ? Format(number, includePlus) : "—";
    private static string Format(decimal value, bool includePlus = false) => (includePlus && value > 0m ? "+" : string.Empty) + value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string FormatTwoDecimals(decimal? value, string suffix = "") => value is { } number ? number.ToString("0.00", CultureInfo.InvariantCulture) + suffix : "—";
    private static string FormatDuration(TimeSpan value) => value.TotalDays >= 1d ? $"{(int)value.TotalDays}d {value:hh\\:mm\\:ss}" : value.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
