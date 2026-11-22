using System.Text;
using WurmMiningCounter.Models;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class SkillStatisticsViewModelTests
{
    [Fact]
    public void SelectedActionCount_InvalidValuesAreUnavailableAndValidInputRecovers()
    {
        using var fixture = StartedFixture("Alice");
        var counter = new MiningSessionCounter();
        var baselines = new MiningStatisticsSessionBaselineStore();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, counter, action => action(), fixture.Clock, baselines);
        counter.Add(fixture.ProfileId("Alice"), "Alice", new MiningEvent(MiningMaterial.Ore));
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();

        foreach (var invalid in new[] { string.Empty, "0", "-1", "abc", "9223372036854775808" })
        {
            viewModel.SelectedActionCountText = invalid;
            Assert.Equal("—", viewModel.TicksPerSelectedActionsDisplay);
        }

        viewModel.SelectedActionCountText = "25";

        Assert.Equal("≈ 25.00", viewModel.TicksPerSelectedActionsDisplay);
    }

    [Fact]
    public void SelectedActionCount_NormalizesBothBelowAndAboveSessionActionCount()
    {
        using var fixture = StartedFixture("Alice");
        var counter = new MiningSessionCounter();
        var baselines = new MiningStatisticsSessionBaselineStore();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, counter, action => action(), fixture.Clock, baselines);
        for (var index = 0; index < 200; index++)
            counter.Add(fixture.ProfileId("Alice"), "Alice", new MiningEvent(MiningMaterial.Ore));
        fixture.AppendSource("Alice", string.Concat(Enumerable.Range(1, 50)
            .Select(index => SkillLine("Mining", 80m + index / 1000m))));
        fixture.Monitor.Poll();

        viewModel.SelectedActionCountText = "25";
        Assert.Equal("≈ 6.25", viewModel.TicksPerSelectedActionsDisplay);
        viewModel.SelectedActionCountText = "400";
        Assert.Equal("≈ 100.00", viewModel.TicksPerSelectedActionsDisplay);
    }

    [Fact]
    public void CompleteBaselineChange_UpdatesTickGainAndSessionTimeDerivedStatistics()
    {
        using var fixture = StartedFixture("Alice");
        var counter = new MiningSessionCounter();
        var baselines = new MiningStatisticsSessionBaselineStore();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, counter, action => action(), fixture.Clock, baselines);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        fixture.AppendSource("Alice", string.Concat(Enumerable.Range(1, 5)
            .Select(index => SkillLine("Mining", 80m + index / 1000m))));
        fixture.Monitor.Poll();

        var newStart = fixture.Clock.GetUtcNow() - TimeSpan.FromMinutes(30);
        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Alice"), 3, 0.003m, newStart);

        Assert.Equal("2", viewModel.MiningTickCountDisplay);
        Assert.Equal("0.001", viewModel.AverageGainPerTickDisplay);
        Assert.Equal("≈ 4.00", viewModel.TicksPerHourDisplay);
    }

    [Fact]
    public void TicksPerHourDisplay_UsesApproximationOnlyWhenAvailable()
    {
        using var fixture = StartedFixture("Alice");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);

        Assert.Equal("—", viewModel.TicksPerHourDisplay);

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();

        Assert.Equal("≈ 1.00", viewModel.TicksPerHourDisplay);
    }

    [Fact]
    public void SessionStatistics_UsesExistingCounterAndRefreshesOnCountChanges()
    {
        using var fixture = StartedFixture("Alice");
        var counter = new MiningSessionCounter();
        var baselines = new MiningStatisticsSessionBaselineStore();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, counter, action => action(), fixture.Clock, baselines);

        counter.Add(fixture.ProfileId("Alice"), "Alice", new MiningEvent(MiningMaterial.Ore));
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();

        Assert.Equal("1", viewModel.SuccessfulActionsDisplay);
        Assert.Equal("1", viewModel.MiningTickCountDisplay);
        Assert.Equal("100.00%", viewModel.TickRatePercentDisplay);
        Assert.Equal("1.00", viewModel.ActionsPerTickDisplay);
        Assert.Equal("≈ 100.00", viewModel.TicksPerSelectedActionsDisplay);

        counter.Reset();

        Assert.Equal("0", viewModel.SuccessfulActionsDisplay);
        Assert.Equal("—", viewModel.TickRatePercentDisplay);
    }

    [Fact]
    public void ObservationStart_IsCapturedAtProfileDiscoveryAndNeverChangesDuringUpdates()
    {
        using var fixture = StartedFixture("Alice");
        var startedAt = Assert.Single(fixture.Monitor.GetSnapshots()).ObservationStartedAtUtc;
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();

        Assert.Equal(startedAt, Assert.Single(fixture.Monitor.GetSnapshots()).ObservationStartedAtUtc);
    }

    [Fact]
    public void LateProfile_UsesItsFirstDiscoveryTimeAsObservationStart()
    {
        using var fixture = new Fixture();
        fixture.Monitor.Start();
        fixture.Clock.Advance(TimeSpan.FromMinutes(7));
        fixture.CreateSource("Bob", SkillLine("Mining", 70m));
        fixture.AppendConsole("Bob", fixture.SourcePath("Bob"));
        fixture.Monitor.Poll();

        Assert.Equal(fixture.Clock.GetUtcNow(), Assert.Single(fixture.Monitor.GetSnapshots()).ObservationStartedAtUtc);
    }

    [Fact]
    public void OneProfile_IsSelectedAutomatically()
    {
        using var fixture = StartedFixture("Alice");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);

        Assert.Equal(fixture.ProfileId("Alice"), viewModel.SelectedProfileId);
    }

    [Fact]
    public void MultipleProfiles_UseDeterministicInitialSelectionAndRetainManualSelection()
    {
        using var fixture = StartedFixture("Bob", "Alice");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);

        Assert.Equal(["Alice", "Bob"], viewModel.Profiles.Select(profile => profile.ProfileName));
        Assert.Equal(fixture.ProfileId("Alice"), viewModel.SelectedProfileId);
        viewModel.SelectedProfileId = fixture.ProfileId("Bob");
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();

        Assert.Equal(fixture.ProfileId("Bob"), viewModel.SelectedProfileId);
    }

    [Fact]
    public void LiveUpdate_UsesPostToUiAndMapsMiningAndOtherSkills()
    {
        using var fixture = StartedFixture("Alice");
        var scheduledActions = new List<Action>();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, scheduledActions.Add, fixture.Clock);

        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m) + SkillLine("Prospecting", 10m));
        fixture.Monitor.Poll();

        Assert.Single(scheduledActions);
        Assert.Equal("0", viewModel.MiningSessionGainDisplay);
        scheduledActions.Single()();

        Assert.Equal("80.001", viewModel.MiningCurrentDisplay);
        Assert.Equal("+0.001", viewModel.MiningLastGainDisplay);
        Assert.Equal("+0.001", viewModel.MiningSessionGainDisplay);
        var other = Assert.Single(viewModel.OtherSkills);
        Assert.Equal("Prospecting", other.SkillName);
        Assert.Equal(10m, other.CurrentValue);
        Assert.Equal(0.001m, other.LastGain);
        Assert.Equal(0.001m, other.SessionGain);
    }

    [Fact]
    public void BootstrapMining_HasZeroTicks()
    {
        using var fixture = StartedFixture("Alice");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);

        Assert.Equal(0, viewModel.MiningTickCount);
        Assert.Equal("0", viewModel.MiningTickCountDisplay);
    }

    [Fact]
    public void MiningIncreases_IncrementOnlyMiningTickCount()
    {
        using var fixture = StartedFixture("Alice");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m) + SkillLine("Mining", 80.002m));
        fixture.Monitor.Poll();

        Assert.Equal(2, viewModel.MiningTickCount);
        Assert.Equal("2", viewModel.MiningTickCountDisplay);
    }

    [Fact]
    public void MiningDecrease_DoesNotIncrementTickCount()
    {
        using var fixture = StartedFixture("Alice");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();
        fixture.AppendSource("Alice", DecreaseLine("Mining", 80m));
        fixture.Monitor.Poll();

        Assert.Equal(1, viewModel.MiningTickCount);
    }

    [Fact]
    public void OtherSkillIncreases_DoNotChangeMiningTickCount()
    {
        using var fixture = StartedFixture("Alice");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);
        fixture.AppendSource("Alice", SkillLine("Prospecting", 10m) + SkillLine("Pickaxe", 20m));
        fixture.Monitor.Poll();

        Assert.Equal(0, viewModel.MiningTickCount);
    }

    [Fact]
    public void SelectedProfile_UsesItsOwnMiningTickCount()
    {
        using var fixture = StartedFixture("Alice", "Bob");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.AppendSource("Bob", SkillLine("Mining", 70.001m) + SkillLine("Mining", 70.002m));
        fixture.Monitor.Poll();

        Assert.Equal(1, viewModel.MiningTickCount);
        viewModel.SelectedProfileId = fixture.ProfileId("Bob");
        Assert.Equal(2, viewModel.MiningTickCount);
    }

    [Fact]
    public void MissingMining_DisplaysDash()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", SkillLine("Prospecting", 10m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice"));
        fixture.Monitor.Start();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);

        Assert.Equal("—", viewModel.MiningTickCountDisplay);
    }

    [Fact]
    public void MiningTickCount_SubtractsProfileBaselineWithoutClamping()
    {
        using var fixture = StartedFixture("Alice");
        var baselines = new MiningStatisticsSessionBaselineStore();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock, baselines);
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m) + SkillLine("Mining", 80.002m) + SkillLine("Mining", 80.003m));
        fixture.Monitor.Poll();

        Assert.Equal(3, viewModel.MiningTickCount);
        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Alice"), 3, 0m, fixture.Clock.GetUtcNow());
        Assert.Equal(0, viewModel.MiningTickCount);
        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Alice"), 1, 0m, fixture.Clock.GetUtcNow());
        Assert.Equal(2, viewModel.MiningTickCount);
        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Alice"), 5, 0m, fixture.Clock.GetUtcNow());
        Assert.Equal(-2, viewModel.MiningTickCount);
    }

    [Fact]
    public void BaselineChange_UpdatesOpenSelectedProfileWithoutLiveSkillUpdate()
    {
        using var fixture = StartedFixture("Alice");
        var baselines = new MiningStatisticsSessionBaselineStore();
        var scheduledActions = new List<Action>();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, scheduledActions.Add, fixture.Clock, baselines);
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m) + SkillLine("Mining", 80.002m) + SkillLine("Mining", 80.003m));
        fixture.Monitor.Poll();
        var liveUpdate = Assert.Single(scheduledActions);
        scheduledActions.Clear();
        liveUpdate();

        Assert.Equal("3", viewModel.MiningTickCountDisplay);

        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Alice"), 3, 0m, fixture.Clock.GetUtcNow());

        Assert.Equal("0", viewModel.MiningTickCountDisplay);
        Assert.Single(scheduledActions);
        var baselineUpdate = scheduledActions.Single();
        scheduledActions.Clear();
        baselineUpdate();
        Assert.Equal("0", viewModel.MiningTickCountDisplay);
    }

    [Fact]
    public void MiningTickBaselines_AreIndependentPerSelectedProfile()
    {
        using var fixture = StartedFixture("Alice", "Bob");
        var baselines = new MiningStatisticsSessionBaselineStore();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock, baselines);
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m) + SkillLine("Mining", 80.002m) + SkillLine("Mining", 80.003m));
        fixture.AppendSource("Bob", SkillLine("Mining", 70.001m) + SkillLine("Mining", 70.002m));
        fixture.Monitor.Poll();
        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Alice"), 2, 0m, fixture.Clock.GetUtcNow());
        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Bob"), 1, 0m, fixture.Clock.GetUtcNow());

        Assert.Equal(1, viewModel.MiningTickCount);
        viewModel.SelectedProfileId = fixture.ProfileId("Bob");
        Assert.Equal(1, viewModel.MiningTickCount);
        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Alice"), 3, 0m, fixture.Clock.GetUtcNow());
        Assert.Equal(1, viewModel.MiningTickCount);
    }

    [Fact]
    public void Detach_IgnoresQueuedAndFutureBaselineChanges()
    {
        using var fixture = StartedFixture("Alice");
        var baselines = new MiningStatisticsSessionBaselineStore();
        var scheduledActions = new List<Action>();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, scheduledActions.Add, fixture.Clock, baselines);
        var tickNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SkillStatisticsViewModel.MiningTickCountDisplay))
                tickNotifications++;
        };
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();
        var liveUpdate = Assert.Single(scheduledActions);
        scheduledActions.Clear();
        liveUpdate();
        tickNotifications = 0;
        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Alice"), 1, 0m, fixture.Clock.GetUtcNow());
        viewModel.DetachLiveSkillUpdates();

        scheduledActions.Single()();
        scheduledActions.Clear();
        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Alice"), 0, 0m, fixture.Clock.GetUtcNow());

        Assert.Equal("1", viewModel.MiningTickCountDisplay);
        Assert.Equal(0, tickNotifications);
        Assert.Empty(scheduledActions);
    }

    [Fact]
    public void RecreatedViewModel_UsesSharedBaselineStore()
    {
        using var fixture = StartedFixture("Alice");
        var baselines = new MiningStatisticsSessionBaselineStore();
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m) + SkillLine("Mining", 80.002m) + SkillLine("Mining", 80.003m));
        fixture.Monitor.Poll();
        baselines.CaptureMiningSessionBaseline(fixture.ProfileId("Alice"), 2, 0m, fixture.Clock.GetUtcNow());

        var first = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock, baselines);
        first.DetachLiveSkillUpdates();
        var reopened = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock, baselines);

        Assert.Equal("1", reopened.MiningTickCountDisplay);
    }

    [Fact]
    public void Decrease_PreservesPositiveMiningGains()
    {
        using var fixture = StartedFixture("Alice");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();
        fixture.AppendSource("Alice", DecreaseLine("Mining", 80m));
        fixture.Monitor.Poll();

        Assert.Equal("80", viewModel.MiningCurrentDisplay);
        Assert.Equal("+0.001", viewModel.MiningLastGainDisplay);
        Assert.Equal("+0.001", viewModel.MiningSessionGainDisplay);
    }

    [Fact]
    public void OtherSkills_AreIndependentAndUseDeterministicDefaultOrdering()
    {
        using var fixture = StartedFixture("Alice");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);
        fixture.AppendSource("Alice", SkillLine("Prospecting", 10m) + SkillLine("Pickaxe", 20m));
        fixture.Monitor.Poll();

        Assert.Equal(["Pickaxe", "Prospecting"], viewModel.OtherSkills.Select(skill => skill.SkillName));
        Assert.Equal(20m, viewModel.OtherSkills[0].CurrentValue);
        Assert.Equal(10m, viewModel.OtherSkills[1].CurrentValue);
    }

    [Fact]
    public void RefreshTimeDerivedMetrics_UsesClockWithoutChangingSkillValues()
    {
        using var fixture = StartedFixture("Alice");
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, action => action(), fixture.Clock);
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();
        var current = viewModel.MiningCurrent;
        var sessionGain = viewModel.MiningSessionGain;

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        viewModel.RefreshTimeDerivedMetrics();

        Assert.Equal(current, viewModel.MiningCurrent);
        Assert.Equal(sessionGain, viewModel.MiningSessionGain);
        Assert.Equal("+0.001", viewModel.MiningGainPerHourDisplay);
        Assert.NotEqual("—", viewModel.MiningTimeToMilestoneDisplay);
    }

    [Fact]
    public void Detach_IgnoresQueuedAndFutureUpdates()
    {
        using var fixture = StartedFixture("Alice");
        var scheduledActions = new List<Action>();
        var viewModel = new SkillStatisticsViewModel(fixture.Monitor, scheduledActions.Add, fixture.Clock);
        fixture.AppendSource("Alice", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();
        viewModel.DetachLiveSkillUpdates();
        viewModel.DetachLiveSkillUpdates();

        Assert.Single(scheduledActions)();
        fixture.AppendSource("Alice", SkillLine("Mining", 80.002m));
        fixture.Monitor.Poll();
        Assert.Equal("80", viewModel.MiningCurrentDisplay);
        Assert.Single(scheduledActions);
    }

    private static Fixture StartedFixture(params string[] profiles)
    {
        var fixture = new Fixture();
        foreach (var profile in profiles)
        {
            fixture.CreateSource(profile, SkillLine("Mining", profile == "Alice" ? 80m : 70m));
            fixture.AppendConsole(profile, fixture.SourcePath(profile));
        }

        fixture.Monitor.Start();
        return fixture;
    }

    private static string SkillLine(string skill, decimal current) =>
        $"[10:00:00] {skill} increased by 0,001000 to {current:0.000000}\n".Replace('.', ',');

    private static string DecreaseLine(string skill, decimal current) =>
        $"[10:00:00] {skill} decreased by 0,001000 to {current:0.000000}\n".Replace('.', ',');

    private sealed class Fixture : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"WurmMiningCounter.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(Root, "players"));
            Clock = new ManualClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
            Resolver = new PlayerProfileIdentityResolver(Path.Combine(Root, "identity-registry"));
            Monitor = new LiveSkillMonitor(
                Path.Combine(Root, "players"),
                TimeSpan.FromDays(1),
                timeProvider: Clock,
                profileIdentityResolver: Resolver);
        }

        public string Root { get; }
        public ManualClock Clock { get; }
        public PlayerProfileIdentityResolver Resolver { get; }
        public LiveSkillMonitor Monitor { get; }

        public PlayerProfileId ProfileId(string profileName) => Monitor.GetSnapshots()
            .Single(snapshot => string.Equals(snapshot.ProfileName, profileName, StringComparison.Ordinal))
            .ProfileId;

        public string SourcePath(string profile) => Path.Combine(Root, "players", profile, "logs", "_Skills.2026-10.txt");

        public void CreateSource(string profile, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath(profile))!);
            File.WriteAllText(SourcePath(profile), content, Utf8);
        }

        public void AppendSource(string profile, string content) => File.AppendAllText(SourcePath(profile), content, Utf8);

        public void AppendConsole(string profile, string sourcePath) =>
            File.AppendAllText(Path.Combine(Root, $"console.{profile}.log"), $"[10:00:00] Writing to {sourcePath}\n", Utf8);

        public void Dispose()
        {
            Monitor.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
