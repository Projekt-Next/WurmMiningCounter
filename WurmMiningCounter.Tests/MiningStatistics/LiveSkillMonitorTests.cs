using System.Text;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.MiningStatistics;

public sealed class LiveSkillMonitorTests
{
    [Fact]
    public void DefaultPollingInterval_Is250Milliseconds() =>
        Assert.Equal(TimeSpan.FromMilliseconds(250), LiveSkillMonitor.DefaultPollingInterval);

    [Fact]
    public void StartupExistingSource_BootstrapsMiningCurrentWithoutSessionGain()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();

        monitor.Start();

        var mining = Mining(Assert.Single(monitor.GetSnapshots()));
        Assert.Equal(80m, mining.CurrentValue);
        Assert.Null(mining.LastGain);
        Assert.Equal(0m, mining.SessionGain);
    }

    [Fact]
    public void StartupKnownSourceWithCaptureFailure_IsNeverReclassifiedAsPostStartOrReplayed()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-09.txt", MiningLine(79m));
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        var sourcePath = fixture.SourcePath("Alice", "_Skills.2026-10.txt");
        fixture.AppendConsole("Alice", sourcePath);
        var captureAttempts = 0;
        using var monitor = new LiveSkillMonitor(
            Path.Combine(fixture.Root, "players"),
            TimeSpan.FromDays(1),
            sourceReaderFactory: path =>
            {
                if (string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase) && captureAttempts++ == 0)
                    throw new IOException("Expected startup capture failure.");
                return new MiningSkillsSourceReader(path);
            },
            profileIdentityResolver: fixture.Resolver);

        monitor.Start();
        monitor.Poll();

        var snapshot = Assert.Single(monitor.GetSnapshots());
        Assert.Equal(LiveSkillSourceState.Incomplete, snapshot.SourceState);
        Assert.Equal(new SkillStateSnapshot("Mining", 79m, null, 0m), Mining(snapshot));
        Assert.Equal(1, captureAttempts);
    }

    [Fact]
    public void InitializationEnumerationFailure_RetriesConfirmedRegistrationWithoutReresolvingOrDuplicatingProfiles()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        var attempts = 0;
        using var monitor = new LiveSkillMonitor(
            Path.Combine(fixture.Root, "players"),
            TimeSpan.FromDays(1),
            profileIdentityResolver: fixture.Resolver,
            enumerateSkillFiles: path =>
            {
                if (string.Equals(path, Path.GetDirectoryName(fixture.SourcePath("Alice", "_Skills.2026-10.txt")), StringComparison.OrdinalIgnoreCase)
                    && attempts++ == 0)
                    throw new IOException("Expected one-time enumeration failure.");
                return Directory.EnumerateFiles(path, "_Skills*.txt", SearchOption.TopDirectoryOnly);
            });

        monitor.Start();
        Assert.Empty(monitor.GetSnapshots());

        monitor.Poll();
        monitor.Poll();

        Assert.Equal(2, attempts);
        Assert.Equal("Alice", Assert.Single(monitor.GetSnapshots()).ProfileName);
    }

    [Fact]
    public void FailedSkillInitializationIsPendingUntilRealActivationAndDisposeClearsItsHealth()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        var attempts = 0;
        using var monitor = new LiveSkillMonitor(
            Path.Combine(fixture.Root, "players"), TimeSpan.FromDays(1),
            profileIdentityResolver: fixture.Resolver,
            enumerateSkillFiles: path =>
            {
                if (attempts++ == 0)
                    throw new IOException("Expected initialization failure.");
                return Directory.EnumerateFiles(path, "_Skills*.txt", SearchOption.TopDirectoryOnly);
            });
        var viewModel = new WurmMiningCounter.ViewModels.MainViewModel(new MiningSessionCounter());
        var health = new WurmMiningCounter.MonitoringHealthState(viewModel, action => action());
        WurmMiningCounter.App.WireIdentityActivationUiHealth(fixture.Resolver, health, new RuntimeDiagnosticService());

        monitor.Start();

        var pending = Assert.Single(fixture.Resolver.GetActivationStatusSnapshot());
        Assert.True(pending.IsPending);
        Assert.False(pending.IsActive);
        Assert.Equal("Monitoring identity error", viewModel.StatusText);

        monitor.Poll();

        Assert.True(Assert.Single(fixture.Resolver.GetActivationStatusSnapshot()).IsActive);
        Assert.Equal("Monitoring", viewModel.StatusText);
        monitor.Dispose();
        Assert.Empty(fixture.Resolver.GetActivationStatusSnapshot());
        Assert.Equal("Monitoring", viewModel.StatusText);
    }

    [Fact]
    public void ReplacementInitializationFailure_RetriesAtEofWithoutOldWriterReplayOrBlockingHealthyProfile()
    {
        using var fixture = new Fixture();
        const string fileName = "_Skills.2026-10.txt";
        fixture.CreateSource("Alice", fileName, MiningLine(80m));
        fixture.CreateSource("Charlie", fileName, MiningLine(70m));
        var aliceSource = fixture.SourcePath("Alice", fileName);
        fixture.AppendConsole("Alice", aliceSource);
        fixture.AppendConsole("Charlie", fixture.SourcePath("Charlie", fileName));
        var failReplacement = false;
        var aliceInitializationAttempts = 0;
        using var monitor = new LiveSkillMonitor(
            Path.Combine(fixture.Root, "players"), TimeSpan.FromDays(1),
            profileIdentityResolver: fixture.Resolver,
            enumerateSkillFiles: path =>
            {
                if (path == Path.GetDirectoryName(aliceSource))
                {
                    aliceInitializationAttempts++;
                    if (failReplacement)
                    {
                        failReplacement = false;
                        throw new IOException("Expected replacement initialization failure.");
                    }
                }
                return Directory.EnumerateFiles(path, "_Skills*.txt", SearchOption.TopDirectoryOnly);
            });
        monitor.Start();
        var oldId = fixture.ProfileId(monitor, "Alice");
        fixture.AppendSource("Alice", fileName, MiningLine(80.001m));
        fixture.AppendConsoleRaw("Alice", "[10:00:01] Writing to ");
        monitor.Poll();
        var retained = monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == oldId);
        var notifications = new List<LiveSkillProfileChangedEventArgs>();
        monitor.ProfileChanged += (_, args) => notifications.Add(args);

        Directory.Move(Path.Combine(fixture.Root, "players", "Alice"), Path.Combine(fixture.Root, "retired-A"));
        fixture.CreateSource("Alice", fileName, MiningLine(90m));
        fixture.AppendSource("Charlie", fileName, MiningLine(70.001m));
        failReplacement = true;
        monitor.Poll();

        Assert.Equal(2, aliceInitializationAttempts);
        var retired = monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == oldId);
        Assert.False(retired.IsHealthy);
        Assert.Equal(retained.Skills, retired.Skills);
        Assert.Contains(notifications, args => args.ProfileId == oldId && !args.Snapshot.IsHealthy);
        Assert.Contains(notifications, args => args.Snapshot.ProfileName == "Charlie"
            && Mining(args.Snapshot).SessionGain == 0.001m);

        fixture.AppendSource("Alice", fileName, MiningLine(90.001m));
        // Completing the old owner's unfinished declaration cannot select B's writer.
        fixture.AppendConsoleRaw("Alice", aliceSource + "\n");
        using (new FileStream(Path.Combine(fixture.Root, "identity-registry", "player-profile-identities.json"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            monitor.Poll();
            monitor.Poll();
        }

        Assert.Equal(3, aliceInitializationAttempts);
        var replacement = Assert.Single(monitor.GetSnapshots().Where(snapshot =>
            snapshot.ProfileName == "Alice" && snapshot.ProfileId != oldId));
        Assert.Null(replacement.ActiveSourcePath);
        Assert.Equal(90.001m, Mining(replacement).CurrentValue);
        Assert.Equal(0m, Mining(replacement).SessionGain);
        Assert.Single(notifications.Where(args => args.ProfileId == replacement.ProfileId));

        fixture.AppendSource("Alice", fileName, MiningLine(90.002m));
        fixture.AppendConsole("Alice", aliceSource);
        monitor.Poll();
        var live = monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == replacement.ProfileId);
        Assert.True(live.IsHealthy);
        Assert.Equal(0.001m, Mining(live).SessionGain);
        Assert.Equal(retained.Skills, monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == oldId).Skills);
    }

    [Fact]
    public void PendingReplacementRequiresCurrentOwnershipEvidenceBeforeRetryingInitialization()
    {
        using var fixture = new Fixture();
        const string fileName = "_Skills.2026-10.txt";
        fixture.CreateSource("Alice", fileName, MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", fileName));
        var evidenceAvailable = true;
        var physicalIdentity = new WindowsDirectoryPhysicalIdentityProvider();
        var resolver = new PlayerProfileIdentityResolver(Path.Combine(fixture.Root, "identity-registry"),
            new DelegateDirectoryPhysicalIdentityProvider(path => evidenceAvailable ? physicalIdentity.GetIdentity(path) : null));
        var fail = false;
        var attempts = 0;
        using var monitor = new LiveSkillMonitor(Path.Combine(fixture.Root, "players"), TimeSpan.FromDays(1),
            profileIdentityResolver: resolver,
            enumerateSkillFiles: path =>
            {
                attempts++;
                if (fail)
                    throw new IOException("Expected pending failure.");
                return Directory.EnumerateFiles(path, "_Skills*.txt", SearchOption.TopDirectoryOnly);
            });
        monitor.Start();
        var oldId = Assert.Single(monitor.GetSnapshots()).ProfileId;
        Directory.Move(Path.Combine(fixture.Root, "players", "Alice"), Path.Combine(fixture.Root, "retired-A"));
        fixture.CreateSource("Alice", fileName, MiningLine(90m));
        fail = true;
        monitor.Poll();
        Assert.Equal(2, attempts);

        evidenceAvailable = false;
        fail = false;
        monitor.Poll();
        monitor.Poll();
        Assert.Equal(2, attempts);
        Assert.Single(monitor.GetSnapshots());

        evidenceAvailable = true;
        monitor.Poll();
        Assert.Equal(3, attempts);
        Assert.Equal(2, monitor.GetSnapshots().Count);
        Assert.Equal(0m, Mining(monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId != oldId)).SessionGain);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingInitializationDiagnostics_AreDeduplicatedUntilErrorKindChangesOrRecovery(bool replacement)
    {
        using var fixture = new Fixture();
        const string fileName = "_Skills.2026-10.txt";
        fixture.CreateSource("Alice", fileName, MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", fileName));
        var diagnostics = new RuntimeDiagnosticService();
        Exception? failure = replacement ? null : new IOException("Expected persistent failure.");
        using var monitor = new LiveSkillMonitor(Path.Combine(fixture.Root, "players"), TimeSpan.FromDays(1),
            diagnostics: diagnostics, profileIdentityResolver: fixture.Resolver,
            enumerateSkillFiles: path => failure is null
                ? Directory.EnumerateFiles(path, "_Skills*.txt", SearchOption.TopDirectoryOnly)
                : throw failure);
        monitor.Start();
        if (replacement)
        {
            Directory.Move(Path.Combine(fixture.Root, "players", "Alice"), Path.Combine(fixture.Root, "retired-A"));
            fixture.CreateSource("Alice", fileName, MiningLine(90m));
            failure = new IOException("Expected replacement failure.");
        }
        for (var poll = 0; poll < 10; poll++)
            monitor.Poll();
        Assert.Single(diagnostics.GetSnapshot());

        failure = new UnauthorizedAccessException("Expected changed failure kind.");
        for (var poll = 0; poll < 5; poll++)
            monitor.Poll();
        Assert.Equal(2, diagnostics.GetSnapshot().Count);
        Assert.Contains("UnauthorizedAccessException", diagnostics.GetSnapshot()[1].Message);

        failure = null;
        monitor.Poll();
        var countAfterRecovery = monitor.GetSnapshots().Count;
        Directory.Move(Path.Combine(fixture.Root, "players", "Alice"), Path.Combine(fixture.Root, "retired-B"));
        fixture.CreateSource("Alice", fileName, MiningLine(95m));
        // The same kind must be reported again for a failure after recovery.
        failure = new UnauthorizedAccessException("Expected new failure after recovery.");
        for (var poll = 0; poll < 5; poll++)
            monitor.Poll();
        Assert.Equal(3, diagnostics.GetSnapshot().Count);
        Assert.Equal(countAfterRecovery, monitor.GetSnapshots().Count);
    }

    [Fact]
    public void PendingInitializationDiagnostics_AreIndependentForEachProfile()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt");
        fixture.CreateSource("Bob", "_Skills.2026-10.txt");
        var diagnostics = new RuntimeDiagnosticService();
        using var monitor = new LiveSkillMonitor(Path.Combine(fixture.Root, "players"), TimeSpan.FromDays(1),
            diagnostics: diagnostics, profileIdentityResolver: fixture.Resolver,
            enumerateSkillFiles: _ => throw new IOException("Expected shared failure kind."));
        monitor.Start();
        for (var poll = 0; poll < 10; poll++)
            monitor.Poll();

        Assert.Empty(monitor.GetSnapshots());
        Assert.Equal(2, diagnostics.GetSnapshot().Count);
    }

    [Fact]
    public void Dispose_ReleasesConfirmedPendingOwner()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        var profilePath = Path.Combine(fixture.Root, "players", "Alice");
        var retiredPath = Path.Combine(fixture.Root, "retired-Alice");
        var monitor = new LiveSkillMonitor(
            Path.Combine(fixture.Root, "players"), TimeSpan.FromDays(1),
            profileIdentityResolver: fixture.Resolver,
            enumerateSkillFiles: _ => throw new IOException("Expected pending initialization failure."));

        monitor.Start();
        monitor.Dispose();

        Directory.Move(profilePath, retiredPath);
        Assert.True(Directory.Exists(retiredPath));
    }

    [Fact]
    public void FailedUnconfirmedRegistration_ReleasesItsLeaseAndDoesNotReactivateAutomatically()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        var resolver = new PlayerProfileIdentityResolver(
            Path.Combine(fixture.Root, "identity-registry"),
            new DelegateDirectoryPhysicalIdentityProvider(_ => null));
        var attempts = 0;
        using var monitor = new LiveSkillMonitor(
            Path.Combine(fixture.Root, "players"),
            TimeSpan.FromDays(1),
            profileIdentityResolver: resolver,
            enumerateSkillFiles: _ =>
            {
                attempts++;
                throw new IOException("Expected persistent enumeration failure.");
            });

        monitor.Start();
        monitor.Poll();

        Assert.Empty(monitor.GetSnapshots());
        Assert.Equal(1, attempts);
        Assert.False(resolver.TryRegisterSource(Path.Combine(fixture.Root, "players", "Alice"), out var owner, out var readGuard));
        Assert.Null(owner);
        Assert.Null(readGuard);
    }

    [Fact]
    public void InitializationFailureForOneProfile_DoesNotAffectAnActiveProfile()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.CreateSource("Bob", "_Skills.2026-10.txt", MiningLine(70m));
        var attempts = 0;
        using var monitor = new LiveSkillMonitor(
            Path.Combine(fixture.Root, "players"),
            TimeSpan.FromDays(1),
            profileIdentityResolver: fixture.Resolver,
            enumerateSkillFiles: path =>
            {
                if (path.EndsWith("Alice\\logs", StringComparison.OrdinalIgnoreCase) && attempts++ == 0)
                    throw new IOException("Expected Alice enumeration failure.");
                return Directory.EnumerateFiles(path, "_Skills*.txt", SearchOption.TopDirectoryOnly);
            });

        monitor.Start();

        Assert.Equal("Bob", Assert.Single(monitor.GetSnapshots()).ProfileName);
        monitor.Poll();
        Assert.Equal(["Alice", "Bob"], monitor.GetSnapshots().Select(snapshot => snapshot.ProfileName));
    }

    [Fact]
    public void StartupExistingSource_PostBoundaryMiningCountsAsLiveGain()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));

        monitor.Poll();

        Assert.Equal(new SkillStateSnapshot("Mining", 80.001m, 0.001m, 0.001m, 1), Mining(Assert.Single(monitor.GetSnapshots())));
    }

    [Fact]
    public void Startup_AllLiveSkillsAreTracked()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt");
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m) + SkillLine("Prospecting", 50.001m) + SkillLine("Body Strength", 30.001m));

        monitor.Poll();

        Assert.Equal(["Body Strength", "Mining", "Prospecting"], Assert.Single(monitor.GetSnapshots()).Skills.Select(skill => skill.SkillName));
    }

    [Fact]
    public void StartupActiveSourceWithoutMining_FallsBackToOlderSourceForCurrentOnly()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-09.txt", MiningLine(79m));
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", SkillLine("Prospecting", 50m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();

        monitor.Start();

        var mining = Mining(Assert.Single(monitor.GetSnapshots()));
        Assert.Equal(79m, mining.CurrentValue);
        Assert.Null(mining.LastGain);
        Assert.Equal(0m, mining.SessionGain);
    }

    [Fact]
    public void BootstrapFallback_DoesNotCreateSessionGain()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-09.txt", MiningLine(79m));
        fixture.CreateSource("Alice", "_Skills.2026-10.txt");
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        Assert.Equal(0m, Mining(Assert.Single(monitor.GetSnapshots())).SessionGain);
    }

    [Fact]
    public void BootstrapFallback_OrdersMonthlyAndDailySourcesByCanonicalSourceDate()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(79m));
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt", MiningLine(79.5m));
        fixture.CreateSource("Alice", "_Skills.2026-11.txt", SkillLine("Prospecting", 50m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-11.txt"));
        using var monitor = fixture.CreateMonitor();

        monitor.Start();

        Assert.Equal(79.5m, Mining(Assert.Single(monitor.GetSnapshots())).CurrentValue);
    }

    [Fact]
    public void MultipleProfiles_AreIsolated()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.CreateSource("Bob", "_Skills.2026-10.txt", MiningLine(70m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        fixture.AppendConsole("Bob", fixture.SourcePath("Bob", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));

        monitor.Poll();

        var snapshots = monitor.GetSnapshots();
        Assert.Equal(0.001m, Mining(snapshots.Single(snapshot => snapshot.ProfileName == "Alice")).SessionGain);
        Assert.Equal(0m, Mining(snapshots.Single(snapshot => snapshot.ProfileName == "Bob")).SessionGain);
    }

    [Fact]
    public void RepeatedWriterDeclaration_DoesNotResetOrDoubleCount()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        var source = fixture.SourcePath("Alice", "_Skills.2026-10.txt");
        fixture.AppendConsole("Alice", source);
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));
        fixture.AppendConsole("Alice", source);

        monitor.Poll();
        monitor.Poll();

        Assert.Equal(new SkillStateSnapshot("Mining", 80.001m, 0.001m, 0.001m, 1), Mining(Assert.Single(monitor.GetSnapshots())));
    }

    [Fact]
    public void SourceSwitchToNewPostStartFile_ReadsFromStartAndPreservesSession()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));
        monitor.Poll();
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt", MiningLine(80.003m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"));

        monitor.Poll();

        Assert.Equal(new SkillStateSnapshot("Mining", 80.003m, 0.001m, 0.002m, 2), Mining(Assert.Single(monitor.GetSnapshots())));
    }

    [Fact]
    public void WriterBeforeSourceCreation_WaitsThenReadsNewSourceFromStart()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"));

        monitor.Poll();
        Assert.Equal(LiveSkillSourceState.WaitingForSource, Assert.Single(monitor.GetSnapshots()).SourceState);

        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt", MiningLine(80.002m));
        monitor.Poll();

        Assert.Equal(new SkillStateSnapshot("Mining", 80.002m, 0.001m, 0.001m, 1), Mining(Assert.Single(monitor.GetSnapshots())));
    }

    [Fact]
    public void SourceSwitchToStartupKnownFile_DoesNotReplayPreBoundaryHistory()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-09.txt", MiningLine(79m));
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-09.txt"));

        monitor.Poll();

        Assert.Equal(new SkillStateSnapshot("Mining", 80m, null, 0m), Mining(Assert.Single(monitor.GetSnapshots())));
    }

    [Fact]
    public void LateDiscoveredProfile_BaselinesExistingHistoryInsteadOfReplayingIt()
    {
        using var fixture = new Fixture();
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));

        monitor.Poll();

        Assert.Equal(new SkillStateSnapshot("Mining", 80m, null, 0m), Mining(Assert.Single(monitor.GetSnapshots())));
    }

    [Fact]
    public void ActiveSourceContinuityLoss_MarksIncompleteWithoutRebaseline()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.WriteSource("Alice", "_Skills.2026-10.txt", MiningLine(81m));

        monitor.Poll();

        var snapshot = Assert.Single(monitor.GetSnapshots());
        Assert.Equal(LiveSkillSourceState.Incomplete, snapshot.SourceState);
        Assert.Equal(80m, Mining(snapshot).CurrentValue);
    }

    [Fact]
    public void NewValidWriterAfterPreviousIncomplete_CanResumeObservingWithoutResettingTracker()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.WriteSource("Alice", "_Skills.2026-10.txt", MiningLine(81m));
        monitor.Poll();
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt", MiningLine(80.002m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"));

        monitor.Poll();

        var snapshot = Assert.Single(monitor.GetSnapshots());
        Assert.Equal(LiveSkillSourceState.Observing, snapshot.SourceState);
        Assert.Equal(new SkillStateSnapshot("Mining", 80.002m, 0.001m, 0.001m, 1), Mining(snapshot));
    }

    [Fact]
    public void PartialConsoleWriterDeclaration_IsProcessedOnlyAfterCompletion()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        var source = fixture.SourcePath("Alice", "_Skills.2026-10.txt");
        fixture.AppendConsoleRaw("Alice", "[10:00:00] Writing to " + source[..^4]);
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        Assert.Equal(LiveSkillSourceState.Unknown, Assert.Single(monitor.GetSnapshots()).SourceState);

        fixture.AppendConsoleRaw("Alice", source[^4..] + "\n");
        monitor.Poll();

        Assert.Equal(LiveSkillSourceState.Observing, Assert.Single(monitor.GetSnapshots()).SourceState);
    }

    [Fact]
    public void ConsoleDeclaration_CRLFAndLF_AreHandled()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsoleRaw("Alice", "[10:00:00] Writing to " + fixture.SourcePath("Alice", "_Skills.2026-10.txt") + "\r\n");
        using var monitor = fixture.CreateMonitor();

        monitor.Start();
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        monitor.Poll();

        Assert.Equal(LiveSkillSourceState.Observing, Assert.Single(monitor.GetSnapshots()).SourceState);
    }

    [Fact]
    public void ConsoleDeclaration_WithUtf8Bom_IsHandled()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsoleRaw("Alice", "\uFEFF[10:00:00] Writing to " + fixture.SourcePath("Alice", "_Skills.2026-10.txt") + "\n");
        using var monitor = fixture.CreateMonitor();

        monitor.Start();

        Assert.Equal(LiveSkillSourceState.Observing, Assert.Single(monitor.GetSnapshots()).SourceState);
    }

    [Fact]
    public void ConsoleReplacementSameLength_IsDetected()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10-11.txt", MiningLine(80m));
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt", MiningLine(90m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10-11.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        fixture.WriteConsole("Alice", $"[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10-12.txt")}\n");
        monitor.Poll();

        var snapshot = Assert.Single(monitor.GetSnapshots());
        Assert.Equal(fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"), snapshot.ActiveSourcePath);
        Assert.Equal(new SkillStateSnapshot("Mining", 80m, null, 0m), Mining(snapshot));
    }

    [Fact]
    public void ConsoleReplacementLongerFile_IsDetected()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt", MiningLine(90m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        fixture.WriteConsole("Alice", $"[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10-12.txt")}\nignored\n");
        monitor.Poll();

        Assert.Equal(fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"), Assert.Single(monitor.GetSnapshots()).ActiveSourcePath);
    }

    [Fact]
    public void ConsoleNormalAppend_DoesNotTriggerReplacement()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt", MiningLine(90m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"));
        monitor.Poll();

        Assert.Equal(fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"), Assert.Single(monitor.GetSnapshots()).ActiveSourcePath);
    }

    [Fact]
    public void ConsoleTruncate_ReestablishesWriterSafely()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt", MiningLine(90m));
        fixture.AppendConsoleRaw("Alice", $"[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10.txt")}\n{new string('x', 100)}\n");
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        fixture.WriteConsole("Alice", $"[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10-12.txt")}\n");
        monitor.Poll();

        Assert.Equal(fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"), Assert.Single(monitor.GetSnapshots()).ActiveSourcePath);
    }

    [Fact]
    public void Replacement_SelectsLastCompleteValidWriterDeclaration()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt", MiningLine(90m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        fixture.WriteConsole("Alice",
            $"[10:00:00] Writing to C:\\outside\\_Skills.2026-10.txt\n" +
            $"[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10.txt")}\n" +
            $"[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10-12.txt")}\n");
        monitor.Poll();

        Assert.Equal(fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"), Assert.Single(monitor.GetSnapshots()).ActiveSourcePath);
    }

    [Fact]
    public void Replacement_DoesNotReplayStartupKnownSkillsHistory()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt", MiningLine(90m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        fixture.WriteConsole("Alice", $"[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10-12.txt")}\n");
        monitor.Poll();

        Assert.Equal(new SkillStateSnapshot("Mining", 80m, null, 0m), Mining(Assert.Single(monitor.GetSnapshots())));
    }

    [Fact]
    public void RepeatedConsoleFailures_ReportOneWarningUntilRecovery()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt");
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        var diagnostics = new RuntimeDiagnosticService();
        var failReads = true;
        using var monitor = new LiveSkillMonitor(
            Path.Combine(fixture.Root, "players"),
            TimeSpan.FromDays(1),
            diagnostics: diagnostics,
            consoleStreamFactory: path => failReads
                ? throw new IOException("Expected console failure.")
                : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
            profileIdentityResolver: fixture.Resolver);

        monitor.Start();
        monitor.Poll();
        monitor.Poll();
        Assert.Single(diagnostics.GetSnapshot().Where(entry => entry.Source == nameof(LiveSkillMonitor)));

        failReads = false;
        monitor.Poll();
        failReads = true;
        monitor.Poll();

        Assert.Equal(2, diagnostics.GetSnapshot().Count(entry => entry.Source == nameof(LiveSkillMonitor)));
    }

    [Fact]
    public void PartialConsoleLineBelowLimit_IsPreserved()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt");
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        var declaration = $"[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10-12.txt")}";
        fixture.AppendConsoleRaw("Alice", declaration[..^4]);
        monitor.Poll();

        Assert.False(monitor.GetConsoleTailStateForTest(fixture.ProfileId(monitor, "Alice")).IsDiscardingOversizedRecord);
        fixture.AppendConsoleRaw("Alice", declaration[^4..] + "\n");
        monitor.Poll();

        Assert.Equal(fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"), Assert.Single(monitor.GetSnapshots()).ActiveSourcePath);
    }

    [Fact]
    public void PartialConsoleLineThenReplacement_DiscardsStalePartialAndUsesReplacementWriter()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10-11.txt", MiningLine(80m));
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt");
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10-11.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendConsoleRaw("Alice", $"[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10-12.txt")}"[..40]);
        monitor.Poll();

        fixture.WriteConsole("Alice", $"[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10-12.txt")}\n");
        monitor.Poll();

        Assert.Equal(fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"), Assert.Single(monitor.GetSnapshots()).ActiveSourcePath);
    }

    [Fact]
    public void ConsoleLineAtLimit_IsHandledAccordingToDefinedBoundary()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            fixture.AppendConsoleRaw("Alice", new string('x', LiveSkillMonitor.MaximumConsoleRecordLength));
            monitor.Poll();

            Assert.Equal(LiveSkillMonitor.MaximumConsoleRecordLength, monitor.GetConsoleTailStateForTest(fixture.ProfileId(monitor, "Alice")).PendingByteCount);
            Assert.False(monitor.GetConsoleTailStateForTest(fixture.ProfileId(monitor, "Alice")).IsDiscardingOversizedRecord);
        }
    }

    [Fact]
    public void OversizedPartialConsoleLine_IsBoundedAndNotParsed()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            fixture.AppendConsoleRaw("Alice", new string('x', LiveSkillMonitor.MaximumConsoleRecordLength + 1));
            monitor.Poll();

            var state = monitor.GetConsoleTailStateForTest(fixture.ProfileId(monitor, "Alice"));
            Assert.Equal(0, state.PendingByteCount);
            Assert.True(state.IsDiscardingOversizedRecord);
            Assert.Equal(fixture.SourcePath("Alice", "_Skills.2026-10.txt"), Assert.Single(monitor.GetSnapshots()).ActiveSourcePath);
        }
    }

    [Fact]
    public void OversizedLineAcrossMultiplePolls_DoesNotGrowUnbounded()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            fixture.AppendConsoleRaw("Alice", new string('x', LiveSkillMonitor.MaximumConsoleRecordLength + 1));
            monitor.Poll();
            fixture.AppendConsoleRaw("Alice", new string('y', LiveSkillMonitor.MaximumConsoleRecordLength + 1));
            monitor.Poll();

            var state = monitor.GetConsoleTailStateForTest(fixture.ProfileId(monitor, "Alice"));
            Assert.Equal(0, state.PendingByteCount);
            Assert.True(state.IsDiscardingOversizedRecord);
        }
    }

    [Fact]
    public void AfterOversizedLineTerminator_NextWriterDeclarationIsProcessed()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.CreateSource("Alice", "_Skills.2026-10-12.txt");
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendConsoleRaw("Alice", new string('x', LiveSkillMonitor.MaximumConsoleRecordLength + 1));
        monitor.Poll();

        fixture.AppendConsoleRaw("Alice", $"\n[10:00:00] Writing to {fixture.SourcePath("Alice", "_Skills.2026-10-12.txt")}\n");
        monitor.Poll();

        Assert.Equal(fixture.SourcePath("Alice", "_Skills.2026-10-12.txt"), Assert.Single(monitor.GetSnapshots()).ActiveSourcePath);
    }

    [Fact]
    public void OversizedRecord_ReportsAtMostOneWarning()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        var diagnostics = new RuntimeDiagnosticService();
        using var monitor = new LiveSkillMonitor(
            Path.Combine(fixture.Root, "players"),
            TimeSpan.FromDays(1),
            diagnostics,
            profileIdentityResolver: fixture.Resolver);
        monitor.Start();
        fixture.AppendConsoleRaw("Alice", new string('x', LiveSkillMonitor.MaximumConsoleRecordLength + 1));
        monitor.Poll();
        fixture.AppendConsoleRaw("Alice", new string('y', LiveSkillMonitor.MaximumConsoleRecordLength + 1));
        monitor.Poll();

        Assert.Single(diagnostics.GetSnapshot().Where(entry => entry.Message.Contains("oversized", StringComparison.Ordinal)));
    }

    [Fact]
    public void BootstrapRunsExactlyOncePerProfile()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-09.txt", MiningLine(79m));
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-09.txt"));
        fixture.AppendSource("Alice", "_Skills.2026-09.txt", MiningLine(79.001m));

        monitor.Poll();

        Assert.Equal(new SkillStateSnapshot("Mining", 79.001m, 0.001m, 0.001m, 1), Mining(Assert.Single(monitor.GetSnapshots())));
    }

    [Fact]
    public void SnapshotContainsAllSkillsAndIsImmutable()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt");
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m) + SkillLine("Mind Logic", 20.001m));
        monitor.Poll();
        var before = Assert.Single(monitor.GetSnapshots());
        fixture.AppendSource("Alice", "_Skills.2026-10.txt", SkillLine("Prospecting", 50.001m));
        monitor.Poll();

        Assert.Equal(["Mind Logic", "Mining"], before.Skills.Select(skill => skill.SkillName));
        Assert.Equal(["Mind Logic", "Mining", "Prospecting"], Assert.Single(monitor.GetSnapshots()).Skills.Select(skill => skill.SkillName));
    }

    [Fact]
    public void Dispose_IsSafeAndIdempotent()
    {
        using var fixture = new Fixture();
        var monitor = fixture.CreateMonitor();

        monitor.Dispose();
        monitor.Dispose();
    }

    [Fact]
    public void NoOpPoll_DoesNotRaiseProfileChanged()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            var changes = new List<LiveSkillProfileChangedEventArgs>();
            monitor.ProfileChanged += (_, args) => changes.Add(args);

            monitor.Poll();

            Assert.Empty(changes);
        }
    }

    [Fact]
    public void SingleSkillUpdate_RaisesOneProfileChanged()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            var changes = new List<LiveSkillProfileChangedEventArgs>();
            monitor.ProfileChanged += (_, args) => changes.Add(args);
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));

            monitor.Poll();

            var change = Assert.Single(changes);
            Assert.Equal(fixture.ProfileId(monitor, "Alice"), change.ProfileId);
            Assert.Equal(80.001m, Mining(change.Snapshot).CurrentValue);
        }
    }

    [Fact]
    public void MultipleSkillUpdatesSameProfileInOnePoll_RaiseOneNotification()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            var changes = new List<LiveSkillProfileChangedEventArgs>();
            monitor.ProfileChanged += (_, args) => changes.Add(args);
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m) + SkillLine("Prospecting", 50.001m) + SkillLine("Pickaxe", 40.001m));

            monitor.Poll();

            Assert.Single(changes);
            Assert.Equal(["Mining", "Pickaxe", "Prospecting"], changes[0].Snapshot.Skills.Select(skill => skill.SkillName));
        }
    }

    [Fact]
    public void TwoChangedProfiles_RaiseOneNotificationEach()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.CreateSource("Bob", "_Skills.2026-10.txt", MiningLine(70m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        fixture.AppendConsole("Bob", fixture.SourcePath("Bob", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        var changes = new List<LiveSkillProfileChangedEventArgs>();
        monitor.ProfileChanged += (_, args) => changes.Add(args);
        fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));
        fixture.AppendSource("Bob", "_Skills.2026-10.txt", MiningLine(70.001m));

        monitor.Poll();

        Assert.Equal(
            [fixture.ProfileId(monitor, "Alice"), fixture.ProfileId(monitor, "Bob")],
            changes.Select(change => change.ProfileId));
    }

    [Fact]
    public void NotificationSnapshotContainsFinalStateOfPoll()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            LiveSkillProfileChangedEventArgs? change = null;
            monitor.ProfileChanged += (_, args) => change = args;
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m) + MiningLine(80.003m));

            monitor.Poll();

            Assert.NotNull(change);
            Assert.Equal(new SkillStateSnapshot("Mining", 80.003m, 0.001m, 0.002m, 2), Mining(change!.Snapshot));
        }
    }

    [Fact]
    public void RepeatedSameWriterDeclaration_DoesNotRaiseNotification()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            var changes = new List<LiveSkillProfileChangedEventArgs>();
            monitor.ProfileChanged += (_, args) => changes.Add(args);
            fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));

            monitor.Poll();

            Assert.Empty(changes);
        }
    }

    [Fact]
    public void SourceSwitchWithoutSkillUpdate_RaisesNotification()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-09.txt", MiningLine(79m));
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        var changes = new List<LiveSkillProfileChangedEventArgs>();
        monitor.ProfileChanged += (_, args) => changes.Add(args);
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-09.txt"));

        monitor.Poll();

        var change = Assert.Single(changes);
        Assert.Equal(fixture.SourcePath("Alice", "_Skills.2026-09.txt"), change.Snapshot.ActiveSourcePath);
        Assert.Equal(LiveSkillSourceState.Observing, change.Snapshot.SourceState);
    }

    [Fact]
    public void ContinuityLoss_RaisesNotificationWithIncompleteState()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            LiveSkillProfileChangedEventArgs? change = null;
            monitor.ProfileChanged += (_, args) => change = args;
            fixture.WriteSource("Alice", "_Skills.2026-10.txt", MiningLine(81m));

            monitor.Poll();

            Assert.NotNull(change);
            Assert.Equal(LiveSkillSourceState.Incomplete, change!.Snapshot.SourceState);
        }
    }

    [Fact]
    public void LateDiscoveredProfile_RaisesOneNotificationAfterInitialization()
    {
        using var fixture = new Fixture();
        using var monitor = fixture.CreateMonitor();
        monitor.Start();
        var changes = new List<LiveSkillProfileChangedEventArgs>();
        monitor.ProfileChanged += (_, args) => changes.Add(args);
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));

        monitor.Poll();

        Assert.Equal(fixture.ProfileId(monitor, "Alice"), Assert.Single(changes).ProfileId);
    }

    [Fact]
    public void HandlerCanCallGetSnapshotsWithoutDeadlock()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            IReadOnlyList<LiveSkillProfileSnapshot>? observed = null;
            monitor.ProfileChanged += (_, _) => observed = monitor.GetSnapshots();
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));

            monitor.Poll();

            Assert.NotNull(observed);
            Assert.Equal(80.001m, Mining(Assert.Single(observed!)).CurrentValue);
        }
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotBreakMonitorOrOtherSubscribers()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            var delivered = 0;
            monitor.ProfileChanged += (_, _) => throw new InvalidOperationException("Expected test subscriber failure.");
            monitor.ProfileChanged += (_, _) => delivered++;
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));
            monitor.Poll();
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.002m));

            monitor.Poll();

            Assert.Equal(2, delivered);
            Assert.Equal(0.002m, Mining(Assert.Single(monitor.GetSnapshots())).SessionGain);
        }
    }

    [Fact]
    public void NotificationSnapshot_IsImmutableAcrossLaterPolls()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            var changes = new List<LiveSkillProfileChangedEventArgs>();
            monitor.ProfileChanged += (_, args) => changes.Add(args);
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));
            monitor.Poll();
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.002m));
            monitor.Poll();

            Assert.Equal(80.001m, Mining(changes[0].Snapshot).CurrentValue);
            Assert.Equal(80.002m, Mining(changes[1].Snapshot).CurrentValue);
        }
    }

    [Fact]
    public void Dispose_IsIdempotentAndNoNotificationOccursAfterDisposeReturns()
    {
        using var fixture = StartedFixture(out var monitor);
        var delivered = 0;
        monitor.ProfileChanged += (_, _) => delivered++;

        monitor.Dispose();
        fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));
        monitor.Poll();
        monitor.Dispose();

        Assert.Equal(0, delivered);
    }

    [Fact]
    public async Task ManualPoll_CallbackDispose_ReturnsWithoutDeadlockAndStopsFurtherPolling()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            var callbackReturned = false;
            var delivered = 0;
            monitor.ProfileChanged += (_, _) =>
            {
                delivered++;
                monitor.Dispose();
                callbackReturned = true;
            };
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));

            await Task.Run(monitor.Poll).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(callbackReturned);
            Assert.Equal(1, delivered);
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.002m));
            monitor.Poll();
            Assert.Equal(1, delivered);
        }
    }

    [Fact]
    public async Task CallbackDispose_PreventsRemainingSubscribersFromStarting()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            var firstInvoked = false;
            var secondInvoked = 0;
            monitor.ProfileChanged += (_, _) =>
            {
                firstInvoked = true;
                monitor.Dispose();
            };
            monitor.ProfileChanged += (_, _) => secondInvoked++;
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));

            await Task.Run(monitor.Poll).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(firstInvoked);
            Assert.Equal(0, secondInvoked);
        }
    }

    [Fact]
    public async Task ExternalDispose_WaitsForActiveCallback()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            using var callbackEntered = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();
            var callbackFinished = false;
            monitor.ProfileChanged += (_, _) =>
            {
                callbackEntered.Set();
                releaseCallback.Wait();
                callbackFinished = true;
            };
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));

            var poll = Task.Run(monitor.Poll);
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
            var dispose = Task.Run(monitor.Dispose);
            Assert.False(dispose.IsCompleted);

            releaseCallback.Set();
            await Task.WhenAll(poll, dispose).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(callbackFinished);
        }
    }

    [Fact]
    public async Task WorkerExecution_CallbackDispose_DoesNotSelfJoinAndSecondDisposeIsSafe()
    {
        using var fixture = StartedFixture(out var monitor);
        using (monitor)
        {
            var callbackReturned = false;
            var delivered = 0;
            monitor.ProfileChanged += (_, _) =>
            {
                delivered++;
                monitor.Dispose();
                callbackReturned = true;
            };
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.001m));

            await Task.Run(monitor.ExecuteWorkerPollForTest).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(callbackReturned);
            monitor.Dispose();
            fixture.AppendSource("Alice", "_Skills.2026-10.txt", MiningLine(80.002m));
            monitor.Poll();
            Assert.Equal(1, delivered);
        }
    }

    private static Fixture StartedFixture(out LiveSkillMonitor monitor)
    {
        var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", MiningLine(80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        monitor = fixture.CreateMonitor();
        monitor.Start();
        return fixture;
    }

    private static SkillStateSnapshot Mining(LiveSkillProfileSnapshot snapshot) =>
        Assert.Single(snapshot.Skills.Where(skill => skill.SkillName == "Mining"));

    private static string MiningLine(decimal current) => SkillLine("Mining", current);

    private static string SkillLine(string skillName, decimal current) =>
        $"[10:00:00] {skillName} increased by 0,001000 to {current:0.000000}\n".Replace('.', ',');

    private sealed class Fixture : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"WurmMiningCounter.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(Root, "players"));
            Resolver = new PlayerProfileIdentityResolver(Path.Combine(Root, "identity-registry"));
        }

        public string Root { get; }

        public PlayerProfileIdentityResolver Resolver { get; }

        public LiveSkillMonitor CreateMonitor() => new(
            Path.Combine(Root, "players"),
            TimeSpan.FromDays(1),
            profileIdentityResolver: Resolver);

        public PlayerProfileId ProfileId(LiveSkillMonitor monitor, string profileName) => monitor.GetSnapshots()
            .Single(snapshot => string.Equals(snapshot.ProfileName, profileName, StringComparison.Ordinal))
            .ProfileId;

        public string SourcePath(string profile, string fileName) => Path.Combine(Root, "players", profile, "logs", fileName);

        public void CreateSource(string profile, string fileName, string text = "")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath(profile, fileName))!);
            File.WriteAllText(SourcePath(profile, fileName), text, Utf8);
        }

        public void AppendSource(string profile, string fileName, string text) => File.AppendAllText(SourcePath(profile, fileName), text, Utf8);

        public void WriteSource(string profile, string fileName, string text) => File.WriteAllText(SourcePath(profile, fileName), text, Utf8);

        public void AppendConsole(string profile, string sourcePath) => AppendConsoleRaw(profile, $"[10:00:00] Writing to {sourcePath}\n");

        public void AppendConsoleRaw(string profile, string text) => File.AppendAllText(Path.Combine(Root, $"console.{profile}.log"), text, Utf8);

        public void WriteConsole(string profile, string text) => File.WriteAllText(Path.Combine(Root, $"console.{profile}.log"), text, Utf8);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
