using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class MainViewModelTests
{
    private static readonly PlayerProfileId ZetaId = PlayerProfileId.CreateNew();
    private static readonly PlayerProfileId AlphaId = PlayerProfileId.CreateNew();
    private static readonly PlayerProfileId BetaId = PlayerProfileId.CreateNew();
    private static readonly PlayerProfileId ZuluId = PlayerProfileId.CreateNew();

    private static PlayerProfileId Id(string profileName) => profileName switch
    {
        "Alice" => EventTransportTestProfileIds.Alice,
        "Bob" => EventTransportTestProfileIds.Bob,
        "bob" => EventTransportTestProfileIds.Bob,
        "zeta" => ZetaId,
        "Alpha" => AlphaId,
        "beta" => BetaId,
        "zulu" => ZuluId,
        _ => throw new ArgumentOutOfRangeException(nameof(profileName))
    };
    [Fact]
    public void ConstructorWithNullCounterThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MainViewModel(null!));
    }

    [Fact]
    public void StatusTextInitiallyIndicatesMonitoringIsUnavailable()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());

        Assert.Equal("Monitoring unavailable", viewModel.StatusText);
    }

    [Fact]
    public void SetStatusChangesStatusText()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());

        viewModel.SetStatus("Monitoring");

        Assert.Equal("Monitoring", viewModel.StatusText);
    }

    [Fact]
    public void SetStatusRaisesPropertyChangedWhenStatusChanges()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        viewModel.SetStatus("Monitoring");

        Assert.Equal([nameof(MainViewModel.StatusText)], changedProperties);
    }

    [Fact]
    public void SetStatusDoesNotRaisePropertyChangedWhenStatusDoesNotChange()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());
        var changeCount = 0;
        viewModel.PropertyChanged += (_, _) => changeCount++;

        viewModel.SetStatus("Monitoring unavailable");

        Assert.Equal(0, changeCount);
    }

    [Fact]
    public void SetStatusWithNullThrowsArgumentNullException()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());

        Assert.Throws<ArgumentNullException>(() => viewModel.SetStatus(null!));
    }

    [Fact]
    public void ConstructorLoadsInitialVeinContexts()
    {
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            initialVeinContexts:
            [
                VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 1103, VeinConfidence.Synchronized),
                VeinSnapshot("Bob", VeinMaterial.Zinc, 24m, 17, VeinConfidence.Tracking)
            ]);

        Assert.Equal(2, viewModel.VeinContexts.Count);
        Assert.Equal(VeinMaterial.Iron, viewModel.VeinContexts.Single(context => context.ProfileName == "Alice").Material);
        Assert.Equal(VeinMaterial.Zinc, viewModel.VeinContexts.Single(context => context.ProfileName == "Bob").Material);
    }

    [Fact]
    public void ConstructorLoadsInitialVeinContextsInOrdinalIgnoreCaseProfileOrder()
    {
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            initialVeinContexts:
            [
                VeinSnapshot("zeta", VeinMaterial.Iron, 12.5m, 1103, VeinConfidence.Synchronized),
                VeinSnapshot("Alice", VeinMaterial.Zinc, 24m, 17, VeinConfidence.Tracking),
                VeinSnapshot("bob", VeinMaterial.Lead, 30m, 9, VeinConfidence.Synchronized)
            ]);

        Assert.Equal(["Alice", "bob", "zeta"], viewModel.VeinContexts.Select(context => context.ProfileName));
    }

    [Fact]
    public void ApplyVeinContextSnapshot_UpsertsWithoutDuplicateProfile()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());

        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 1103, VeinConfidence.Synchronized));
        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 1102, VeinConfidence.Tracking));

        var context = Assert.Single(viewModel.VeinContexts);
        Assert.Equal("Alice", context.ProfileName);
        Assert.Equal(1102, Assert.IsType<RemainingMeasurement.Exact>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, context.Confidence);
    }

    [Fact]
    public void ProfilePanels_ComposesMiningAndVeinContextForTheSameProfile()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 1103, VeinConfidence.Synchronized));

        var panel = Assert.Single(viewModel.ProfilePanels);
        Assert.Equal("Alice", panel.ProfileName);
        Assert.Single(panel.Materials);
        Assert.Equal(1, panel.TotalCount);
        Assert.True(panel.HasVeinContext);
        Assert.NotNull(panel.VeinContext);
    }

    [Fact]
    public void ProfilePanels_OmitsVeinContextWhenNoActiveContextExists()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        var panel = Assert.Single(viewModel.ProfilePanels);

        Assert.False(panel.HasVeinContext);
        Assert.Null(panel.VeinContext);
    }

    [Fact]
    public void ProfilePanels_HidesMiningInspirationsWhenCountIsZero()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(
            counter,
            miningInspirationCounter: new MiningInspirationCounter());

        var panel = Assert.Single(viewModel.ProfilePanels);

        Assert.Equal(0, panel.MiningInspirationCount);
        Assert.False(panel.HasMiningInspirations);
    }

    [Fact]
    public void ProfilePanels_ShowsMiningInspirationTotalForMatchingProfile()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var inspirations = new MiningInspirationCounter();
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Ore);
        var viewModel = new MainViewModel(counter, miningInspirationCounter: inspirations);

        var panel = Assert.Single(viewModel.ProfilePanels);

        Assert.Equal(1, panel.MiningInspirationCount);
        Assert.True(panel.HasMiningInspirations);
    }

    [Fact]
    public void ProfilePanels_KeepMiningInspirationsIndependentPerProfile()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Shards));
        var inspirations = new MiningInspirationCounter();
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Ore);
        inspirations.Add(EventTransportTestProfileIds.Bob, "Bob", MiningMaterial.Shards);
        inspirations.Add(EventTransportTestProfileIds.Bob, "Bob", MiningMaterial.StoneShards);
        var viewModel = new MainViewModel(counter, miningInspirationCounter: inspirations);

        Assert.Equal(1, viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Alice").MiningInspirationCount);
        Assert.Equal(2, viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Bob").MiningInspirationCount);
    }

    [Fact]
    public async Task ResetMiningInspirationAsync_ResetsConfiguredProfileAndRefreshesOnlyItsMoi()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Shards));
        var inspirations = new MiningInspirationCounter();
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Ore);
        inspirations.Add(EventTransportTestProfileIds.Bob, "Bob", MiningMaterial.Shards);
        var resetProfiles = new List<PlayerProfileId>();
        var viewModel = new MainViewModel(
            counter,
            miningInspirationCounter: inspirations,
            resetMiningInspirationAsync: profileId =>
            {
                resetProfiles.Add(profileId);
                inspirations.ResetProfile(profileId);
                return Task.CompletedTask;
            });

        await viewModel.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice);

        Assert.Equal([EventTransportTestProfileIds.Alice], resetProfiles);
        Assert.Equal(0, viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Alice").MiningInspirationCount);
        Assert.Equal(1, viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Bob").MiningInspirationCount);
        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].Materials[MiningMaterial.Ore]);
    }

    [Fact]
    public async Task ResetMiningInspirationAsync_RefreshesOnlyAfterConfiguredOperationCompletes()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var inspirations = new MiningInspirationCounter();
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Ore);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new MainViewModel(
            counter,
            miningInspirationCounter: inspirations,
            resetMiningInspirationAsync: async profileId =>
            {
                started.TrySetResult();
                await release.Task;
                inspirations.ResetProfile(profileId);
            });

        var reset = viewModel.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice);
        await started.Task;

        Assert.False(reset.IsCompleted);
        Assert.Equal(1, Assert.Single(viewModel.ProfilePanels).MiningInspirationCount);

        release.TrySetResult();
        await reset;

        Assert.Equal(0, Assert.Single(viewModel.ProfilePanels).MiningInspirationCount);
    }

    [Fact]
    public async Task ResetMiningInspirationAsync_UsesMiningDispatcherWithoutChangingVeinContext()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var inspirations = new MiningInspirationCounter();
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Ore);
        var tracker = new MiningInspirationTracker(inspirations);
        var lane = new MiningEventLane(new MiningEventProcessor(new MiningEventParser(), counter, tracker));
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(lane, context);
        var veinProcessor = new VeinContextProcessor();
        CreateIronContext(veinProcessor, "Alice");
        var viewModel = new MainViewModel(
            counter,
            veinContextProcessor: veinProcessor,
            postToUi: action => action(),
            miningInspirationCounter: inspirations,
            resetMiningInspirationAsync: _ => dispatcher.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice));
        try
        {
            dispatcher.Handoff(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Event.txt", 1,
                ["You start to mine forwards."]));
            Assert.Equal(1, context.ProfileQueueCount);
            await viewModel.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice);

            Assert.Empty(inspirations.GetProfileSnapshots());
            Assert.True(Assert.Single(viewModel.ProfilePanels).HasVeinContext);
            Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
            Assert.Equal(1, context.ProfileQueueCount);
        }
        finally { await dispatcher.CompleteAsync(); }
    }

    [Fact]
    public async Task ResetMiningInspirationAsync_FailureDoesNotRefreshOrReportSuccess()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var inspirations = new MiningInspirationCounter();
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Ore);
        var viewModel = new MainViewModel(
            counter,
            miningInspirationCounter: inspirations,
            resetMiningInspirationAsync: _ => Task.FromException(new InvalidOperationException("fault")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice));

        Assert.Equal(1, Assert.Single(viewModel.ProfilePanels).MiningInspirationCount);
    }

    [Fact]
    public async Task ResetMiningInspirationAsync_NewMoiAfterResetIsDisplayed()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var inspirations = new MiningInspirationCounter();
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Ore);
        var viewModel = new MainViewModel(
            counter,
            miningInspirationCounter: inspirations,
            resetMiningInspirationAsync: profileId =>
            {
                inspirations.ResetProfile(profileId);
                return Task.CompletedTask;
            });

        await viewModel.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice);
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Shards);
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Shards));

        Assert.Equal(1, Assert.Single(viewModel.ProfilePanels).MiningInspirationCount);
    }

    [Fact]
    public async Task ResetMiningInspirationAsync_RequiresConfiguredOperation()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice));
    }

    [Fact]
    public void ApplyVeinContextSnapshot_LocalExactDecrementDisplaysDeltaOne()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter(), delayAsync: NeverEndingDelayAsync);
        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 6710, VeinConfidence.Synchronized));

        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 6709, VeinConfidence.Tracking),
            isLocalExactDecrement: true);

        Assert.Equal("↓1 6709", Assert.Single(viewModel.VeinContexts).RemainingText);
    }

    [Fact]
    public void ApplyVeinContextSnapshot_LocalExactDecrementSeriesAccumulatesFromItsBaseValue()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter(), delayAsync: NeverEndingDelayAsync);
        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 6710, VeinConfidence.Synchronized));

        foreach (var remaining in new[] { 6709, 6708, 6707 })
        {
            viewModel.ApplyVeinContextSnapshot(
                EventTransportTestProfileIds.Alice,
                VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, remaining, VeinConfidence.Tracking),
                isLocalExactDecrement: true);
        }

        Assert.Equal("↓3 6707", Assert.Single(viewModel.VeinContexts).RemainingText);
    }

    [Fact]
    public async Task ApplyVeinContextSnapshot_LocalExactDecrementExpiresAfterDelay()
    {
        var delay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerPosted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            postToUi: action =>
            {
                action();
                timerPosted.TrySetResult(true);
            },
            delayAsync: (_, _) => delay.Task);
        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 6710, VeinConfidence.Synchronized));
        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 6707, VeinConfidence.Tracking),
            isLocalExactDecrement: true);

        Assert.Equal("↓3 6707", Assert.Single(viewModel.VeinContexts).RemainingText);
        delay.SetResult(true);
        await timerPosted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("6707", Assert.Single(viewModel.VeinContexts).RemainingText);
    }

    [Fact]
    public void ApplyVeinContextSnapshot_ReprospectingDoesNotDisplayLocalExactDelta()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter(), delayAsync: NeverEndingDelayAsync);
        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 6710, VeinConfidence.Synchronized));
        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 6707, VeinConfidence.Tracking),
            isLocalExactDecrement: true);

        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 6200, VeinConfidence.Synchronized));

        Assert.Equal("6200", Assert.Single(viewModel.VeinContexts).RemainingText);
    }

    [Fact]
    public void ApplyVeinContextSnapshot_UpsertMaintainsOrdinalIgnoreCaseProfileOrder()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());

        viewModel.ApplyVeinContextSnapshot(
            ZetaId,
            VeinSnapshot("zeta", VeinMaterial.Iron, 12.5m, 1103, VeinConfidence.Synchronized));
        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Zinc, 24m, 17, VeinConfidence.Tracking));
        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Bob,
            VeinSnapshot("bob", VeinMaterial.Lead, 30m, 9, VeinConfidence.Synchronized));
        viewModel.ApplyVeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            VeinSnapshot("Alice", VeinMaterial.Zinc, 24m, 8, VeinConfidence.Tracking));

        Assert.Equal(["Alice", "bob", "zeta"], viewModel.VeinContexts.Select(context => context.ProfileName));
        Assert.Equal(8, Assert.IsType<RemainingMeasurement.Exact>(viewModel.VeinContexts[0].Remaining).Value);
    }

    [Fact]
    public void ApplyVeinContextSnapshot_RemovesProfileWhenSnapshotIsNull()
    {
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            initialVeinContexts:
            [VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 1103, VeinConfidence.Synchronized)]);

        viewModel.ApplyVeinContextSnapshot(EventTransportTestProfileIds.Alice, null);

        Assert.Empty(viewModel.VeinContexts);
    }

    [Fact]
    public void ApplyVeinContextSnapshot_RemovePreservesOrdinalIgnoreCaseProfileOrder()
    {
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            initialVeinContexts:
            [
                VeinSnapshot("zeta", VeinMaterial.Iron, 12.5m, 1103, VeinConfidence.Synchronized),
                VeinSnapshot("Alice", VeinMaterial.Zinc, 24m, 17, VeinConfidence.Tracking),
                VeinSnapshot("bob", VeinMaterial.Lead, 30m, 9, VeinConfidence.Synchronized)
            ]);

        viewModel.ApplyVeinContextSnapshot(EventTransportTestProfileIds.Bob, null);

        Assert.Equal(["Alice", "zeta"], viewModel.VeinContexts.Select(context => context.ProfileName));
    }

    [Fact]
    public void ActiveContextChanged_UpsertsVeinContextThroughScheduler()
    {
        var processor = new VeinContextProcessor();
        var scheduledActions = new List<Action>();
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            veinContextProcessor: processor,
            postToUi: scheduledActions.Add);

        CreateIronContext(processor, "Alice");

        Assert.Empty(viewModel.VeinContexts);
        Assert.Equal(3, scheduledActions.Count);
        foreach (var scheduledAction in scheduledActions)
            scheduledAction();

        var context = Assert.Single(viewModel.VeinContexts);
        Assert.Equal("Alice", context.ProfileName);
        Assert.Equal(VeinMaterial.Iron, context.Material);
    }

    [Fact]
    public void ActiveContextChanged_EmergenceShowsVeinContextInMatchingProfilePanel()
    {
        var processor = new VeinContextProcessor();
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            veinContextProcessor: processor,
            postToUi: action => action());

        processor.Process(new OrderedEventLine(
            EventTransportTestProfileIds.Bob,
            "Bob",
            1,
            new EventLogLine(EventTransportTestProfileIds.Bob, "Bob", "Event.txt", "A vein of pure lead emerges here.")));

        var panel = Assert.Single(viewModel.ProfilePanels);
        Assert.Equal("Bob", panel.ProfileName);
        Assert.True(panel.HasVeinContext);
        Assert.Equal(VeinMaterial.Lead, panel.VeinContext!.Material);
        Assert.Equal("Material only", panel.VeinContext.ConfidenceText);
    }

    [Fact]
    public void PlainRockProspectingResultChanged_ShowsPlainRockInMatchingProfilePanel()
    {
        var processor = new VeinContextProcessor();
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            veinContextProcessor: processor,
            postToUi: action => action());

        ProcessLine(processor, "Alice", 1, "You start to gather fragments of the rock.");
        ProcessLine(processor, "Alice", 2, "You would mine stone shards here.");
        ProcessLine(processor, "Alice", 3, "It is of acceptable quality.");

        var panel = Assert.Single(viewModel.ProfilePanels);
        Assert.True(panel.HasProspectingResult);
        Assert.False(panel.HasVeinContext);
        var result = Assert.IsType<ProfilePlainRockProspectingResultViewModel>(panel.PlainRockProspectingResult);
        Assert.Equal("Plain Rock", result.Title);
        Assert.Equal(
            ProfileVeinContextViewModel.FormatQuality(
                new QualityMeasurement.Grade(ProspectingQualityGrade.Acceptable)),
            result.QualityText);
    }

    [Fact]
    public void PlainRockProspectingResults_AreIsolatedPerProfileAndYieldToSynchronizedVein()
    {
        var processor = new VeinContextProcessor();
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            veinContextProcessor: processor,
            postToUi: action => action());

        ProcessLine(processor, "Alice", 1, "You start to gather fragments of the rock.");
        ProcessLine(processor, "Alice", 2, "You would mine stone shards here.");
        ProcessLine(processor, "Alice", 3, "It has a max quality of 12.5.");
        ProcessLine(processor, "Bob", 1, "You start to gather fragments of the rock.");
        ProcessLine(processor, "Bob", 2, "You would mine iron ore here.");
        ProcessLine(processor, "Bob", 3, "It has a max quality of 12.5.");
        ProcessLine(processor, "Bob", 4, "You will be able to mine here 1103 more times.");

        var alice = viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Alice");
        var bob = viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Bob");
        Assert.True(alice.HasPlainRockProspectingResult);
        Assert.False(alice.HasVeinContext);
        Assert.False(bob.HasPlainRockProspectingResult);
        Assert.True(bob.HasVeinContext);

        ProcessLine(processor, "Alice", 4, "You start to gather fragments of the rock.");
        ProcessLine(processor, "Alice", 5, "You would mine iron ore here.");
        ProcessLine(processor, "Alice", 6, "It has a max quality of 12.5.");
        ProcessLine(processor, "Alice", 7, "You will be able to mine here 1103 more times.");

        alice = viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Alice");
        Assert.False(alice.HasPlainRockProspectingResult);
        Assert.True(alice.HasVeinContext);
    }

    [Fact]
    public void ActiveContextChanged_RemovesVeinContextThroughSchedulerWhenCleared()
    {
        var processor = new VeinContextProcessor();
        var scheduledActions = new List<Action>();
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            veinContextProcessor: processor,
            postToUi: scheduledActions.Add);
        CreateIronContext(processor, "Alice");
        Assert.Equal(3, scheduledActions.Count);
        foreach (var scheduledAction in scheduledActions)
            scheduledAction();
        scheduledActions.Clear();

        processor.Invalidate(EventTransportTestProfileIds.Alice, "Alice");

        Assert.Single(viewModel.VeinContexts);
        Assert.Single(scheduledActions)();
        Assert.Empty(viewModel.VeinContexts);
    }

    [Fact]
    public void ActiveContextChanged_UsesInjectedSchedulerBeforeUpdatingCollection()
    {
        var processor = new VeinContextProcessor();
        var schedulerCallCount = 0;
        Action? scheduledAction = null;
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            veinContextProcessor: processor,
            postToUi: action =>
            {
                schedulerCallCount++;
                scheduledAction = action;
            });

        CreateIronContext(processor, "Alice");

        Assert.Equal(3, schedulerCallCount);
        Assert.Empty(viewModel.VeinContexts);
        Assert.NotNull(scheduledAction);
        scheduledAction();
        Assert.Single(viewModel.VeinContexts);
    }

    [Fact]
    public void DetachVeinContextUpdates_PreventsQueuedAndFutureUpdates()
    {
        var processor = new VeinContextProcessor();
        var scheduledActions = new List<Action>();
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            veinContextProcessor: processor,
            postToUi: scheduledActions.Add);

        CreateIronContext(processor, "Alice");
        viewModel.DetachVeinContextUpdates();
        Assert.Equal(3, scheduledActions.Count);
        foreach (var scheduledAction in scheduledActions)
            scheduledAction();

        Assert.Empty(viewModel.VeinContexts);
        processor.Invalidate(EventTransportTestProfileIds.Alice, "Alice");
        Assert.Equal(3, scheduledActions.Count);
    }

    [Fact]
    public void ProfileAppearingLaterInSnapshotIsShown()
    {
        var counter = new MiningSessionCounter();
        var viewModel = new MainViewModel(counter);
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Ore));

        viewModel.Refresh();

        var profile = Assert.Single(viewModel.Profiles);
        Assert.Equal("Bob", profile.ProfileName);
        Assert.Equal(1, profile.Materials.Single().Count);
    }

    [Fact]
    public void MiningYieldUpdatesReadModelThroughDispatcherWithoutWaitingForRefreshTimer()
    {
        var counter = new MiningSessionCounter();
        var scheduledActions = new List<Action>();
        var viewModel = new MainViewModel(counter, postToUi: scheduledActions.Add);

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));

        Assert.Empty(viewModel.Profiles);
        Assert.Single(scheduledActions)();
        var profile = Assert.Single(viewModel.Profiles);
        Assert.Equal(1, profile.TotalCount);
        Assert.Equal(1, profile.Materials.Single().Count);
    }

    [Fact]
    public void ByproductUpdatesProfilePanelWithoutWaitingForRefreshTimer()
    {
        var counter = new MiningSessionCounter();
        var viewModel = new MainViewModel(counter);

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.SourceCrystal));

        var panel = Assert.Single(viewModel.ProfilePanels);
        Assert.Equal(0, panel.TotalCount);
        Assert.Equal(1, Assert.Single(panel.Materials).Count);
        Assert.Equal(MiningMaterial.SourceCrystal, panel.Materials.Single().Material);
    }

    [Fact]
    public void SessionResetUpdatesReadModelWithoutWaitingForRefreshTimer()
    {
        var counter = new MiningSessionCounter();
        var viewModel = new MainViewModel(counter);
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));

        counter.Reset();

        Assert.Empty(viewModel.Profiles);
        Assert.Empty(viewModel.ProfilePanels);
    }

    [Fact]
    public void UnchangedFallbackRefreshDoesNotReplaceMiningReadModelOrPanels()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);
        var profile = Assert.Single(viewModel.Profiles);
        var panel = Assert.Single(viewModel.ProfilePanels);
        var profileChanges = 0;
        var panelChanges = 0;
        viewModel.Profiles.CollectionChanged += (_, _) => profileChanges++;
        ((System.Collections.Specialized.INotifyCollectionChanged)viewModel.ProfilePanels)
            .CollectionChanged += (_, _) => panelChanges++;

        viewModel.Refresh();

        Assert.Equal(0, profileChanges);
        Assert.Equal(0, panelChanges);
        Assert.Same(profile, Assert.Single(viewModel.Profiles));
        Assert.Same(panel, Assert.Single(viewModel.ProfilePanels));
    }

    [Fact]
    public void RefreshCreatesProfilesAndMaterialsFromCounter()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ruby));
        var viewModel = new MainViewModel(counter);

        viewModel.Refresh();

        var profile = Assert.Single(viewModel.Profiles);
        Assert.Equal("Alice", profile.ProfileName);
        Assert.Equal(2, profile.Materials.Count);
        Assert.Contains(profile.Materials, material =>
            material.Material == MiningMaterial.Ore && material.Count == 1);
        Assert.Contains(profile.Materials, material =>
            material.Material == MiningMaterial.Ruby && material.Count == 1);
        Assert.Equal(1, profile.TotalCount);
    }

    [Fact]
    public void ProfileTotalCountEqualsSingleMaterialCount()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        var profile = Assert.Single(viewModel.Profiles);

        Assert.Equal(2, profile.TotalCount);
    }

    [Fact]
    public void ProfileTotalCountSumsMultipleMaterials()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ruby));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ruby));
        var viewModel = new MainViewModel(counter);

        var profile = Assert.Single(viewModel.Profiles);

        Assert.Equal(1, profile.TotalCount);
    }

    [Fact]
    public void RefreshUpdatesProfileTotalCountAfterNewMining()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        viewModel.Refresh();

        Assert.Equal(2, Assert.Single(viewModel.Profiles).TotalCount);
    }

    [Fact]
    public void RefreshShowsOnlyPositiveCounts()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        viewModel.Refresh();

        Assert.All(viewModel.Profiles.SelectMany(profile => profile.Materials), material =>
            Assert.True(material.Count > 0));
    }

    [Fact]
    public void RefreshKeepsProfileWithCumulativeTotalAfterProfileMaterialCountersReset()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));
        var viewModel = new MainViewModel(counter);
        counter.ResetProfileMaterialCounters(EventTransportTestProfileIds.Alice);

        viewModel.Refresh();

        var profile = Assert.Single(viewModel.Profiles);
        Assert.Equal("Alice", profile.ProfileName);
        Assert.Equal(1, profile.TotalCount);
        Assert.Empty(profile.Materials);
    }

    [Fact]
    public void RefreshKeepsProfileWithOnlySourceCrystalAndZeroTotal()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.SourceCrystal));
        var viewModel = new MainViewModel(counter);

        var profile = Assert.Single(viewModel.Profiles);
        Assert.Equal("Alice", profile.ProfileName);
        Assert.Equal(0, profile.TotalCount);
        var material = Assert.Single(profile.Materials);
        Assert.Equal(MiningMaterial.SourceCrystal, material.Material);
        Assert.Equal(1, material.Count);
    }

    [Fact]
    public void RefreshSortsProfilesAlphabetically()
    {
        var counter = new MiningSessionCounter();
        counter.Add(ZuluId, "zulu", Event(MiningMaterial.Ore));
        counter.Add(AlphaId, "Alpha", Event(MiningMaterial.Ore));
        counter.Add(BetaId, "beta", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        viewModel.Refresh();

        Assert.Equal(["Alpha", "beta", "zulu"], viewModel.Profiles.Select(profile => profile.ProfileName));
    }

    [Fact]
    public void RefreshSortsMaterialsByMiningMaterial()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ruby));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        viewModel.Refresh();

        var materials = Assert.Single(viewModel.Profiles).Materials;
        Assert.Equal(
            [MiningMaterial.Ore, MiningMaterial.Flint, MiningMaterial.Ruby],
            materials.Select(material => material.Material));
    }

    [Fact]
    public void SubsequentRefreshReflectsNewData()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Diamond));
        viewModel.Refresh();

        Assert.Equal(2, viewModel.Profiles.Single(profile => profile.ProfileName == "Alice")
            .Materials.Single(material => material.Material == MiningMaterial.Ore).Count);
        Assert.Equal(1, viewModel.Profiles.Single(profile => profile.ProfileName == "Bob")
            .Materials.Single(material => material.Material == MiningMaterial.Diamond).Count);
    }

    [Fact]
    public async Task ResetSessionClearsCounterAndProfiles()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        Assert.Equal(1, Assert.Single(viewModel.Profiles).TotalCount);

        await viewModel.ResetSessionAsync();

        Assert.Empty(counter.GetSnapshot());
        Assert.Empty(viewModel.Profiles);
    }

    [Fact]
    public void ResetProfileMaterialCountersPreservesTheProfileTotalVeinContextAndOtherProfiles()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Diamond));
        var viewModel = new MainViewModel(
            counter,
            initialVeinContexts:
            [
                VeinSnapshot("Alice", VeinMaterial.Iron, 12.5m, 1103, VeinConfidence.Synchronized),
                VeinSnapshot("Bob", VeinMaterial.Zinc, 12.5m, 1103, VeinConfidence.Synchronized)
            ]);

        viewModel.ResetProfileMaterialCounters(EventTransportTestProfileIds.Alice);

        var alice = viewModel.Profiles.Single(profile => profile.ProfileName == "Alice");
        Assert.Equal(1, alice.TotalCount);
        Assert.Empty(alice.Materials);
        Assert.Contains(viewModel.VeinContexts, context =>
            context.ProfileName == "Alice" && context.Material == VeinMaterial.Iron);
        var alicePanel = viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Alice");
        Assert.Empty(alicePanel.Materials);
        Assert.True(alicePanel.HasVeinContext);
        var bob = viewModel.Profiles.Single(profile => profile.ProfileName == "Bob");
        Assert.Equal(0, bob.TotalCount);
        Assert.Equal(1, bob.Materials.Single().Count);
        Assert.Contains(viewModel.VeinContexts, context =>
            context.ProfileName == "Bob" && context.Material == VeinMaterial.Zinc);
    }

    [Fact]
    public void ResetMaterialsPreservesPendingMoiAndAllNonMaterialState()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));
        var inspirations = new MiningInspirationCounter();
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Ore);
        var veinProcessor = new VeinContextProcessor();
        CreateIronContext(veinProcessor, "Alice");
        ProcessLine(veinProcessor, "Alice", 5, "You start to mine forwards.");
        var clearPendingCalls = 0;
        var viewModel = new MainViewModel(
            counter,
            veinContextProcessor: veinProcessor,
            postToUi: action => action(),
            clearMiningInspirationPending: _ => clearPendingCalls++,
            miningInspirationCounter: inspirations,
            initialNearbyScans:
            [
                new NearbyScanSnapshot(
                    EventTransportTestProfileIds.Alice,
                    "Alice",
                    "iron",
                    [new NearbyProspectingEntry("iron", null)],
                    false)
            ]);

        viewModel.ResetMaterials(EventTransportTestProfileIds.Alice, [MiningMaterial.Ore]);

        var profile = Assert.Single(viewModel.Profiles);
        Assert.Equal(1, profile.TotalCount);
        Assert.Equal(MiningMaterial.Flint, Assert.Single(profile.Materials).Material);
        Assert.Equal(0, clearPendingCalls);
        Assert.Equal(1, Assert.Single(viewModel.ProfilePanels).MiningInspirationCount);
        Assert.True(Assert.Single(viewModel.ProfilePanels).HasVeinContext);
        Assert.True(Assert.Single(viewModel.ProfilePanels).HasNearbyScan);
        Assert.True(veinProcessor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void ResetProfileMaterialCountersStillClearsPendingMoi()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var clearedProfiles = new List<PlayerProfileId>();
        var viewModel = new MainViewModel(
            counter,
            clearMiningInspirationPending: clearedProfiles.Add);

        viewModel.ResetProfileMaterialCounters(EventTransportTestProfileIds.Alice);

        Assert.Equal([EventTransportTestProfileIds.Alice], clearedProfiles);
        Assert.Empty(Assert.Single(viewModel.Profiles).Materials);
    }

    [Fact]
    public async Task ResetVeinContextAsyncPreservesMiningCountersAndDoesNotChangeOtherProfiles()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Diamond));
        var processor = new VeinContextProcessor();
        CreateIronContext(processor, "Alice");
        ProcessLine(processor, "Bob", 1, "You start to gather fragments of the rock.");
        ProcessLine(processor, "Bob", 2, "You would mine zinc ore here.");
        ProcessLine(processor, "Bob", 3, "It has a max quality of 12.5.");
        ProcessLine(processor, "Bob", 4, "You will be able to mine here 1103 more times.");
        using var coordinator = new EventLineCoordinator();
        coordinator.RegisterConsumer(processor);
        var viewModel = new MainViewModel(
            counter,
            veinContextProcessor: processor,
            postToUi: action => action(),
            resetVeinContextAsync: profileId => coordinator.ResetProfileAsync(profileId));

        await viewModel.ResetVeinContextAsync(EventTransportTestProfileIds.Alice);

        Assert.DoesNotContain(viewModel.VeinContexts, context => context.ProfileName == "Alice");
        Assert.Contains(viewModel.VeinContexts, context =>
            context.ProfileName == "Bob" && context.Material == VeinMaterial.Zinc);
        Assert.False(viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Alice").HasVeinContext);
        Assert.True(viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Bob").HasVeinContext);
        var alice = viewModel.Profiles.Single(profile => profile.ProfileName == "Alice");
        Assert.Equal(1, alice.TotalCount);
        Assert.Equal(1, alice.Materials.Single().Count);
        var bob = viewModel.Profiles.Single(profile => profile.ProfileName == "Bob");
        Assert.Equal(0, bob.TotalCount);
        Assert.Equal(1, bob.Materials.Single().Count);
    }

    [Fact]
    public async Task ResetSessionAsyncInvokesConfiguredCallback()
    {
        var callbackCount = 0;
        var viewModel = new MainViewModel(
            new MiningSessionCounter(),
            () =>
            {
                callbackCount++;
                return Task.CompletedTask;
            });

        await viewModel.ResetSessionAsync();

        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task ResetSessionAsyncRefreshesOnlyAfterCallbackCompletes()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var callbackStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new MainViewModel(
            counter,
            async () =>
            {
                callbackStarted.TrySetResult(true);
                await releaseCallback.Task;
                counter.Reset();
            });

        var resetTask = viewModel.ResetSessionAsync();
        await callbackStarted.Task;

        Assert.False(resetTask.IsCompleted);
        Assert.Equal(1, Assert.Single(viewModel.Profiles).TotalCount);

        releaseCallback.TrySetResult(true);
        await resetTask;

        Assert.Empty(counter.GetSnapshot());
        Assert.Empty(viewModel.Profiles);
    }

    [Fact]
    public async Task ResetSessionAsyncCallbackFailureDoesNotRefreshEarly()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(
            counter,
            () => Task.FromException(new InvalidOperationException("Simulated reset failure.")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.ResetSessionAsync());

        Assert.Equal(1, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
        Assert.Equal(1, Assert.Single(viewModel.Profiles).TotalCount);
    }

    [Fact]
    public async Task ResetSessionAsyncWithoutCallbackUsesCounterResetFallback()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        await viewModel.ResetSessionAsync();

        Assert.Empty(counter.GetSnapshot());
        Assert.Empty(viewModel.Profiles);
    }

    [Fact]
    public async Task ProfileReappearsWhenMiningOccursAfterReset()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        await viewModel.ResetSessionAsync();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        viewModel.Refresh();

        var profile = Assert.Single(viewModel.Profiles);
        Assert.Equal("Alice", profile.ProfileName);
        Assert.Equal(1, profile.Materials.Single().Count);
    }

    [Fact]
    public void ProfilesRemainIndependent()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        viewModel.Refresh();

        Assert.Equal(1, viewModel.Profiles.Single(profile => profile.ProfileName == "Alice")
            .Materials.Single(material => material.Material == MiningMaterial.Ore).Count);
        Assert.Equal(2, viewModel.Profiles.Single(profile => profile.ProfileName == "Bob")
            .Materials.Single(material => material.Material == MiningMaterial.Ore).Count);
        Assert.Equal(1, viewModel.Profiles.Single(profile => profile.ProfileName == "Alice").TotalCount);
        Assert.Equal(2, viewModel.Profiles.Single(profile => profile.ProfileName == "Bob").TotalCount);
    }

    [Fact]
    public void GetSnapshotStillReturnsCounterData()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);

        var snapshot = viewModel.GetSnapshot();

        Assert.Equal(1, snapshot[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
    }

    [Fact]
    public void SameNameProfiles_HaveIndependentPanelsAndMaterialResets()
    {
        var first = PlayerProfileId.CreateNew(); var second = PlayerProfileId.CreateNew(); const string name = "Twin";
        var counter = new MiningSessionCounter(); counter.Add(first, name, Event(MiningMaterial.Ore)); counter.Add(second, name, Event(MiningMaterial.Flint));
        var viewModel = new MainViewModel(counter);
        Assert.Equal(2, viewModel.ProfilePanels.Count);
        Assert.Equal(1, viewModel.ProfilePanels.Single(panel => panel.ProfileId == first).TotalCount);
        Assert.Equal(0, viewModel.ProfilePanels.Single(panel => panel.ProfileId == second).TotalCount);
        viewModel.ResetProfileMaterialCounters(first);
        Assert.Empty(viewModel.ProfilePanels.Single(panel => panel.ProfileId == first).Materials);
        Assert.Equal(MiningMaterial.Flint, Assert.Single(viewModel.ProfilePanels.Single(panel => panel.ProfileId == second).Materials).Material);
        Assert.Equal(0, counter.GetProfileSnapshots()[second].TotalCount);
    }

    private static MiningEvent Event(MiningMaterial material) => new(material);

    private static Task NeverEndingDelayAsync(TimeSpan _, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private static void CreateIronContext(VeinContextProcessor processor, string profileName)
    {
        ProcessLine(processor, profileName, 1, "You start to gather fragments of the rock.");
        ProcessLine(processor, profileName, 2, "You would mine iron ore here.");
        ProcessLine(processor, profileName, 3, "It has a max quality of 12.5.");
        ProcessLine(processor, profileName, 4, "You will be able to mine here 1103 more times.");
    }

    private static void ProcessLine(
        VeinContextProcessor processor,
        string profileName,
        long sequenceNumber,
        string text)
    {
        processor.Process(new OrderedEventLine(
            Id(profileName),
            profileName,
            sequenceNumber,
            new EventLogLine(Id(profileName), profileName, "event.log", text)));
    }

    private static VeinContextSnapshot VeinSnapshot(
        string profileName,
        VeinMaterial material,
        decimal quality,
        int remaining,
        VeinConfidence confidence)
    {
        return new VeinContextSnapshot(
            Id(profileName),
            profileName,
            material,
            new QualityMeasurement.ExactMax(quality),
            new RemainingMeasurement.Exact(remaining),
            confidence);
    }
}
