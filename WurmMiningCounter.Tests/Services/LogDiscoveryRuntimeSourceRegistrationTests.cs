using System.Collections.Concurrent;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.Services;

public sealed class LogDiscoveryRuntimeSourceRegistrationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task DiscoveryOwnerConflictSuspendsEventWithoutStoppingOtherProfiles(bool ambiguity, bool readInFlight, bool handoffInFlight)
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-event-conflict-" + Guid.NewGuid().ToString("N"));
        try
        {
            var players = Path.Combine(root, "players");
            var alice = Path.Combine(players, "Alice");
            var bob = Path.Combine(players, "Bob");
            var carol = Path.Combine(players, "Carol");
            var aliceLogs = Path.Combine(alice, "logs");
            var bobLogs = Path.Combine(bob, "logs");
            var carolLogs = Path.Combine(carol, "logs");
            var unavailable = Path.Combine(root, "unavailable", "players");
            foreach (var directory in new[] { aliceLogs, bobLogs, carolLogs })
                Directory.CreateDirectory(directory);
            var sharedFile = Path.Combine(aliceLogs, "_Event.2026-10.txt");
            var carolFile = Path.Combine(carolLogs, "_Event.2026-10.txt");
            File.WriteAllText(sharedFile, "history\n");
            File.WriteAllText(carolFile, "history\n");
            var bobObserved = false;
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var locator = new WurmLogLocationLocator(Directory.Exists,
                path => path == players ? bobObserved ? ambiguity ? [alice, bob, carol] : [bob, carol] : [alice, carol] : Directory.EnumerateDirectories(path),
                path => path == bobLogs ? aliceLogs : path,
                new WindowsDirectoryPhysicalIdentityProvider(), () => players, _ => [],
                _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
                Directory.EnumerateFiles,
                path => path == unavailable ? WurmLogDirectoryProbeResult.AccessDenied
                    : Directory.Exists(path) ? WurmLogDirectoryProbeResult.Exists : WurmLogDirectoryProbeResult.Missing);
            var service = new LogDiscoverySnapshotService(locator, resolver);
            KnownLogLocation Location(string path) => new()
                { Id = path, Enabled = true, Game = "Test", Type = KnownLogLocations.DirectType, Path = path };
            var first = service.Discover([Location(players)], []);
            Assert.True(first.IsComplete);
            var outputs = new ConcurrentQueue<EventPipelineOutput>();
            var clock = new ControlledEventRetirementTests.ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var blockRead = false;
            var blockHandoff = false;
            var reader = new EventLogReader();
            await using var events = new EventPipeline(players, output =>
            {
                if (blockHandoff && output is EventReadBatch batch && batch.CanonicalPath == sharedFile)
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Timed out waiting to release the test handoff.");
                }
                outputs.Enqueue(output);
                return EventPipelineHandoffResult.Complete;
            }, reader, clock, read: (path, offset, length, fingerprint, pending) =>
            {
                if (blockRead && path == sharedFile)
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Timed out waiting to release the test read.");
                }
                return reader.Read(path, offset, length, fingerprint, pending);
            }, profileIdentityResolver: resolver, sourceCandidates: first.SourceCandidates);
            events.Start();
            var checkpoint = Assert.Single(events.SourceCheckpoints, state => state.CanonicalPath == sharedFile);
            Assert.Equal("Alice", checkpoint.ProfileName);
            Assert.Equal(new FileInfo(sharedFile).Length, checkpoint.ReadOffset);
            File.AppendAllText(sharedFile, "before conflict\n");
            events.RunCycleForTest();
            Assert.Equal(["before conflict"], Assert.Single(outputs.OfType<EventReadBatch>()).Lines);
            checkpoint = Assert.Single(events.SourceCheckpoints, state => state.CanonicalPath == sharedFile);
            outputs.Clear();

            Task? inFlight = null;
            if (readInFlight || handoffInFlight)
            {
                blockRead = readInFlight;
                blockHandoff = handoffInFlight;
                File.AppendAllText(sharedFile, "admitted before conflict\n");
                inFlight = Task.Run(events.RunCycleForTest);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            bobObserved = true;
            var partial = service.Discover([Location(players), Location(unavailable)], []);
            Assert.False(partial.IsComplete);
            Assert.Contains(partial.Profiles, profile => profile.ProfileDirectoryPath == alice && !profile.IsCurrent);
            if (ambiguity)
                Assert.Equal(2, Assert.Single(partial.Discovery.SourceOwnershipAmbiguities).Claims.Count);
            else
            {
                Assert.Contains(partial.Profiles, profile => profile.ProfileDirectoryPath == bob && profile.IsCurrent);
                var claimants = partial.SourceCandidates.Where(candidate => candidate.ProfileDirectoryPath == alice || candidate.ProfileDirectoryPath == bob).ToArray();
                Assert.Equal(2, claimants.Length);
                Assert.All(claimants, candidate => Assert.Equal(sharedFile, Assert.Single(candidate.EventFiles).FilePath));
            }
            var publication = service.GetCurrentSnapshot()!;
            events.PublishDiscoverySnapshot(publication.Version, publication.Snapshot.SourceCandidates, publication.Snapshot.Discovery.SourceOwnershipAmbiguities, false);
            events.AddSourceCandidates(publication.Version, partial.SourceCandidates);
            bobObserved = false;
            var recovered = service.Discover([Location(players)], []);
            Assert.True(recovered.IsComplete);
            var newer = service.GetCurrentSnapshot()!;
            if (inFlight is not null)
            {
                release.Set();
                await inFlight.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(["admitted before conflict"], Assert.Single(outputs.OfType<EventReadBatch>()).Lines);
                checkpoint = Assert.Single(events.SourceCheckpoints, state => state.CanonicalPath == sharedFile);
                outputs.Clear();
            }

            File.AppendAllText(sharedFile, "uncertain\n");
            File.AppendAllText(carolFile, "independent\n");
            events.RunCycleForTest();
            Assert.DoesNotContain(outputs.OfType<EventReadBatch>(), batch => batch.CanonicalPath == sharedFile);
            Assert.Equal(checkpoint, Assert.Single(events.SourceCheckpoints, state => state.CanonicalPath == sharedFile));
            var carolBatch = Assert.Single(outputs.OfType<EventReadBatch>(), batch => batch.CanonicalPath == carolFile);
            Assert.Equal("Carol", carolBatch.ProfileName);
            Assert.Equal(["independent"], carolBatch.Lines);
            var newSegment = Path.Combine(aliceLogs, "_Event.2026-10-16.txt");
            File.WriteAllText(newSegment, "new segment\n");
            events.DiscoverForTest();
            events.RunCycleForTest();
            Assert.DoesNotContain(events.SourceCheckpoints, state => state.CanonicalPath == newSegment);
            Assert.DoesNotContain(outputs.OfType<EventReadBatch>(), batch => batch.CanonicalPath == newSegment);
            events.PublishDiscoverySnapshot(publication.Version - 1, first.SourceCandidates, first.Discovery.SourceOwnershipAmbiguities, true);
            events.RunCycleForTest();
            Assert.Equal(checkpoint, Assert.Single(events.SourceCheckpoints, state => state.CanonicalPath == sharedFile));
            await events.StopAsync();

            var laterOutputs = new ConcurrentQueue<EventPipelineOutput>();
            await using var laterEvents = new EventPipeline(players, output =>
            {
                laterOutputs.Enqueue(output);
                return EventPipelineHandoffResult.Complete;
            }, new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: recovered.SourceCandidates);
            laterEvents.Start();
            laterEvents.PublishDiscoverySnapshot(newer.Version, newer.Snapshot.SourceCandidates, newer.Snapshot.Discovery.SourceOwnershipAmbiguities, true);
            laterEvents.PublishDiscoverySnapshot(publication.Version, publication.Snapshot.SourceCandidates, publication.Snapshot.Discovery.SourceOwnershipAmbiguities, false);
            File.AppendAllText(sharedFile, "after newer snapshot\n");
            laterEvents.RunCycleForTest();
            Assert.Equal(["after newer snapshot"], Assert.Single(laterOutputs.OfType<EventReadBatch>()).Lines);
            await laterEvents.StopAsync();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConfirmedExistingOwnerResumesFromCheckpointOnlyAfterAuthoritativeDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-event-resume-" + Guid.NewGuid().ToString("N"));
        try
        {
            var players = Path.Combine(root, "players");
            var alice = Path.Combine(players, "Alice");
            var bob = Path.Combine(players, "Bob");
            var carol = Path.Combine(players, "Carol");
            var aliceLogs = Path.Combine(alice, "logs");
            var bobLogs = Path.Combine(bob, "logs");
            var carolLogs = Path.Combine(carol, "logs");
            var unavailable = Path.Combine(root, "unavailable", "players");
            foreach (var directory in new[] { aliceLogs, bobLogs, carolLogs }) Directory.CreateDirectory(directory);
            var shared = Path.Combine(aliceLogs, "_Event.2026-10.txt");
            var independent = Path.Combine(carolLogs, "_Event.2026-10.txt");
            File.WriteAllText(shared, "history\n");
            File.WriteAllText(independent, "history\n");
            var profileMode = 0;
            var identityMode = 0;
            var provider = new WindowsDirectoryPhysicalIdentityProvider();
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var viewModel = new MainViewModel(new MiningSessionCounter());
            App.WireIdentityActivationUiHealth(resolver, viewModel, action => action(), new RuntimeDiagnosticService());
            var locator = new WurmLogLocationLocator(Directory.Exists,
                path => path == players ? profileMode switch { 1 => [bob, carol], 2 => [alice, bob, carol], _ => [alice, carol] } : Directory.EnumerateDirectories(path),
                path => path == bobLogs ? aliceLogs : path,
                new DelegateDirectoryPhysicalIdentityProvider(path => path == alice && identityMode != 0
                    ? identityMode == 1 ? null : "replacement" : provider.GetIdentity(path)), () => players, _ => [],
                _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
                Directory.EnumerateFiles, path => path == unavailable ? WurmLogDirectoryProbeResult.AccessDenied
                    : Directory.Exists(path) ? WurmLogDirectoryProbeResult.Exists : WurmLogDirectoryProbeResult.Missing);
            var service = new LogDiscoverySnapshotService(locator, resolver);
            KnownLogLocation Location(string path) => new() { Id = path, Enabled = true, Game = "Test", Type = KnownLogLocations.DirectType, Path = path };
            var first = service.Discover([Location(players)], []);
            var outputs = new ConcurrentQueue<EventPipelineOutput>();
            var clock = new ControlledEventRetirementTests.ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            await using var events = new EventPipeline(players, output =>
            {
                outputs.Enqueue(output);
                return EventPipelineHandoffResult.Complete;
            }, new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: first.SourceCandidates);
            events.Start();
            var checkpoint = Assert.Single(events.SourceCheckpoints, state => state.CanonicalPath == shared);
            void Publish(LogDiscoverySnapshot snapshot)
            {
                var publication = service.GetCurrentSnapshot()!;
                Assert.Same(snapshot, publication.Snapshot);
                events.PublishDiscoverySnapshot(publication.Version, snapshot.SourceCandidates, snapshot.Discovery.SourceOwnershipAmbiguities,
                    snapshot.IsComplete && snapshot.Discovery.Uncertainties.Count == 0 && snapshot.Discovery.Rejections.Count == 0);
                events.AddSourceCandidates(publication.Version, snapshot.SourceCandidates);
            }
            profileMode = 1;
            var conflict = service.Discover([Location(players), Location(unavailable)], []);
            var oldPublication = service.GetCurrentSnapshot()!;
            Assert.False(conflict.IsComplete);
            Publish(conflict);
            events.RunCycleForTest();
            File.AppendAllText(shared, "during conflict\n");
            File.AppendAllText(independent, "carol\n");

            profileMode = 0;
            Publish(service.Discover([Location(players), Location(unavailable)], []));
            events.RunCycleForTest();
            Assert.Equal(checkpoint, Assert.Single(events.SourceCheckpoints, state => state.CanonicalPath == shared));
            Assert.Equal(["carol"], Assert.Single(outputs.OfType<EventReadBatch>()).Lines);

            profileMode = 2;
            var ambiguous = service.Discover([Location(players)], []);
            Assert.NotEmpty(ambiguous.Discovery.SourceOwnershipAmbiguities);
            Publish(ambiguous);
            events.RunCycleForTest();
            Assert.Equal("Monitoring - ownership conflict; Event paused", viewModel.StatusText);
            Assert.Equal(checkpoint, Assert.Single(events.SourceCheckpoints, state => state.CanonicalPath == shared));

            profileMode = 0;
            foreach (var mode in new[] { 1, 2 })
            {
                identityMode = mode;
                var unverified = service.Discover([Location(players)], []);
                Publish(unverified);
                events.RunCycleForTest();
                Assert.Equal(checkpoint, Assert.Single(events.SourceCheckpoints, state => state.CanonicalPath == shared));
            }

            identityMode = 0;
            var resolved = service.Discover([Location(players)], []);
            Assert.True(resolved.IsComplete);
            Publish(resolved);
            events.PublishDiscoverySnapshot(oldPublication.Version, oldPublication.Snapshot.SourceCandidates,
                oldPublication.Snapshot.Discovery.SourceOwnershipAmbiguities, false);
            events.RunCycleForTest();
            var batch = Assert.Single(outputs.OfType<EventReadBatch>(), item => item.CanonicalPath == shared);
            Assert.Equal("Monitoring", viewModel.StatusText);
            Assert.Equal(checkpoint.ProfileId, batch.ProfileId);
            Assert.Equal("Alice", batch.ProfileName);
            Assert.Equal(["during conflict"], batch.Lines);
            events.RunCycleForTest();
            Assert.Single(outputs.OfType<EventReadBatch>(), item => item.CanonicalPath == shared);
            File.AppendAllText(shared, "after resume\n");
            events.RunCycleForTest();
            Assert.Equal(["during conflict", "after resume"], outputs.OfType<EventReadBatch>()
                .Where(item => item.CanonicalPath == shared).SelectMany(item => item.Lines).ToArray());
            await events.StopAsync();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AddOnlyRefreshRegistersNewEventAndSkillsWithoutReplayingOrDuplicatingExistingSources()
    {
        using var files = new SourceFixture();
        var candidate = files.CreateCandidate("standard", "Alice", includeEvent: true, includeSkills: true, writerDeclared: true);
        var clock = new MutableTimeProvider();
        var published = new ConcurrentQueue<EventPipelineOutput>();
        using var skills = files.CreateSkillsMonitor([]);
        await using var events = new EventPipeline(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), output =>
        {
            published.Enqueue(output);
            return EventPipelineHandoffResult.Complete;
        }, new EventLogReader(), clock, profileIdentityResolver: files.Resolver, sourceCandidates: []);

        skills.Start();
        events.Start();
        Assert.True(skills.AddSourceCandidates(2, [candidate]));
        Assert.True(events.AddSourceCandidates(2, [candidate]));
        Assert.False(skills.AddSourceCandidates(2, [candidate]));
        Assert.False(events.AddSourceCandidates(1, [candidate]));

        skills.Poll();
        clock.Advance(TimeSpan.FromSeconds(1));
        events.RunCycleForTest();

        var skill = Assert.Single(skills.GetSnapshots());
        var checkpoint = Assert.Single(events.SourceCheckpoints);
        Assert.Equal(skill.ProfileId, checkpoint.ProfileId);
        Assert.Equal(LiveSkillSourceState.Observing, skill.SourceState);
        Assert.Empty(published);
        Assert.Equal(new FileInfo(candidate.EventFiles.Single().FilePath).Length, checkpoint.ReadOffset);

        File.AppendAllText(candidate.EventFiles.Single().FilePath, "live\n");
        events.RunCycleForTest();
        Assert.Equal(["live"], Assert.Single(published.OfType<EventReadBatch>()).Lines);
        Assert.False(events.AddSourceCandidates(3, [candidate]));
        Assert.False(skills.AddSourceCandidates(3, [candidate]));
        Assert.Single(events.SourceCheckpoints);
        Assert.Single(skills.GetSnapshots());
        await events.StopAsync();
    }

    [Fact]
    public void AddOnlyRefreshWaitsForAValidWriterAndDoesNotActivateHistoricalCandidates()
    {
        using var files = new SourceFixture();
        var current = files.CreateCandidate("standard", "Alice", includeEvent: false, includeSkills: true, writerDeclared: false);
        var historical = files.CreateCandidate("historical", "Bob", includeEvent: false, includeSkills: true, writerDeclared: true)
            with { IsCurrent = false };
        using var skills = files.CreateSkillsMonitor([]);

        skills.Start();
        Assert.True(skills.AddSourceCandidates(2, [current, historical]));
        skills.Poll();
        var waiting = Assert.Single(skills.GetSnapshots());
        Assert.Equal("Alice", waiting.ProfileName);
        Assert.Null(waiting.ActiveSourcePath);

        File.WriteAllText(current.ConsolePath, $"[10:00:00] Writing to {current.SkillsFiles.Single().FilePath}\n");
        skills.Poll();
        Assert.Equal(current.SkillsFiles.Single().FilePath, Assert.Single(skills.GetSnapshots()).ActiveSourcePath);
        Assert.False(skills.AddSourceCandidates(3, [historical]));
        Assert.Single(skills.GetSnapshots());
    }

    [Fact]
    public void AddOnlyRefreshKeepsSameNamedPhysicalProfilesSeparateAndIgnoresDuplicateCandidate()
    {
        using var files = new SourceFixture();
        var first = files.CreateCandidate("first", "Same", includeEvent: false, includeSkills: true, writerDeclared: true);
        var second = files.CreateCandidate("second", "Same", includeEvent: false, includeSkills: true, writerDeclared: true);
        using var skills = files.CreateSkillsMonitor([]);

        skills.Start();
        Assert.True(skills.AddSourceCandidates(2, [first, second, first with
        {
            Origins = [new WurmLogLocationOrigin("alias", "alias", "Alias", false)]
        }]));
        skills.Poll();

        var snapshots = skills.GetSnapshots();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(2, snapshots.Select(snapshot => snapshot.ProfileId).Distinct().Count());
        Assert.All(snapshots, snapshot => Assert.Equal("Same", snapshot.ProfileName));
    }

    [Fact]
    public async Task AddOnlyIncompleteRefreshNeverRetiresExistingEventOrSkillsSources()
    {
        using var files = new SourceFixture();
        var existing = files.CreateCandidate("existing", "Alice", includeEvent: true, includeSkills: true, writerDeclared: true);
        var partial = files.CreateCandidate("partial", "Bob", includeEvent: true, includeSkills: true, writerDeclared: true);
        var clock = new MutableTimeProvider();
        using var skills = files.CreateSkillsMonitor([existing]);
        await using var events = files.CreateEventPipeline([existing], clock);

        skills.Start();
        events.Start();
        var existingSkill = Assert.Single(skills.GetSnapshots()).ProfileId;
        var existingEvent = Assert.Single(events.SourceCheckpoints).ProfileId;

        Assert.True(skills.AddSourceCandidates(2, [partial]));
        Assert.True(events.AddSourceCandidates(2, [partial]));
        skills.Poll();
        clock.Advance(TimeSpan.FromSeconds(1));
        events.RunCycleForTest();

        Assert.Contains(skills.GetSnapshots(), snapshot => snapshot.ProfileId == existingSkill);
        Assert.Contains(events.SourceCheckpoints, checkpoint => checkpoint.ProfileId == existingEvent);
        Assert.Equal(2, skills.GetSnapshots().Count);
        Assert.Equal(2, events.SourceCheckpoints.Count);
        await events.StopAsync();
    }

    [Fact]
    public async Task CurrentCandidateRegistersEventAndSkillsWithTheSameRuntimeIdentity()
    {
        using var files = new SourceFixture();
        var candidate = files.CreateCandidate("standard", "Alice", includeEvent: true, includeSkills: true, writerDeclared: true,
            informationalProfileId: new PlayerProfileId(Guid.NewGuid()));
        var discovered = new ConcurrentQueue<EventPipelineSourceStatus>();
        using var skills = files.CreateSkillsMonitor([candidate]);
        await using var events = files.CreateEventPipeline([candidate]);
        events.SourceDiscovered += discovered.Enqueue;

        skills.Start();
        events.Start();

        var skill = Assert.Single(skills.GetSnapshots());
        var source = Assert.Single(discovered);
        Assert.Equal(source.ProfileId, skill.ProfileId);
        Assert.NotEqual(candidate.InformationalProfileId, skill.ProfileId);
        Assert.Equal(LiveSkillSourceState.Observing, skill.SourceState);

        await events.StopAsync();
    }

    [Fact]
    public async Task SameNamedPhysicalCandidatesRemainSeparateForBothRuntimeConsumers()
    {
        using var files = new SourceFixture();
        var first = files.CreateCandidate("first", "Same", includeEvent: true, includeSkills: true, writerDeclared: true);
        var second = files.CreateCandidate("second", "Same", includeEvent: true, includeSkills: true, writerDeclared: true);
        var discovered = new ConcurrentQueue<EventPipelineSourceStatus>();
        using var skills = files.CreateSkillsMonitor([first, second]);
        await using var events = files.CreateEventPipeline([first, second]);
        events.SourceDiscovered += discovered.Enqueue;

        skills.Start();
        events.Start();

        Assert.Equal(2, skills.GetSnapshots().Select(snapshot => snapshot.ProfileId).Distinct().Count());
        Assert.Equal(2, discovered.Select(source => source.ProfileId).Distinct().Count());
        Assert.All(skills.GetSnapshots(), snapshot => Assert.Equal("Same", snapshot.ProfileName));

        await events.StopAsync();
    }

    [Fact]
    public async Task StandardSteamAndUnlimitedCandidatesStartThroughTheSharedContract()
    {
        using var files = new SourceFixture();
        var standard = files.CreateCandidate("standard", "Standard", includeEvent: true, includeSkills: true, writerDeclared: true);
        var steam = files.CreateCandidate("gamedata", "Steam", includeEvent: true, includeSkills: true, writerDeclared: true);
        var unlimited = files.CreateCandidate(Path.Combine("WurmLauncher", "PlayerFiles"), "Unlimited",
            includeEvent: true, includeSkills: true, writerDeclared: true);
        var discovered = new ConcurrentQueue<EventPipelineSourceStatus>();
        using var skills = files.CreateSkillsMonitor([standard, steam, unlimited]);
        await using var events = files.CreateEventPipeline([standard, steam, unlimited]);
        events.SourceDiscovered += discovered.Enqueue;

        skills.Start();
        events.Start();

        Assert.Equal(3, skills.GetSnapshots().Count);
        Assert.Equal(3, discovered.Count);
        Assert.All(skills.GetSnapshots(), snapshot => Assert.Equal(LiveSkillSourceState.Observing, snapshot.SourceState));

        await events.StopAsync();
    }

    [Fact]
    public void NullInformationalIdentityStillRegistersSkillsThroughRuntimeOwnership()
    {
        using var files = new SourceFixture();
        var candidate = files.CreateCandidate("standard", "Alice", includeEvent: false, includeSkills: true, writerDeclared: true);
        using var skills = files.CreateSkillsMonitor([candidate]);

        skills.Start();

        var snapshot = Assert.Single(skills.GetSnapshots());
        Assert.Null(candidate.InformationalProfileId);
        Assert.NotEqual(default, snapshot.ProfileId);
        Assert.Equal(LiveSkillSourceState.Observing, snapshot.SourceState);
    }

    [Fact]
    public void UnlimitedCandidateWithoutWriterDeclarationDoesNotActivateSkills()
    {
        using var files = new SourceFixture();
        var candidate = files.CreateCandidate(Path.Combine("WurmLauncher", "PlayerFiles"), "Unlimited",
            includeEvent: false, includeSkills: true, writerDeclared: false);
        using var skills = files.CreateSkillsMonitor([candidate]);

        skills.Start();

        var snapshot = Assert.Single(skills.GetSnapshots());
        Assert.Null(snapshot.ActiveSourcePath);
        Assert.Equal(LiveSkillSourceState.Unknown, snapshot.SourceState);
    }

    [Fact]
    public async Task HistoricalCandidateDoesNotStartOrReleaseCurrentSources()
    {
        using var files = new SourceFixture();
        var current = files.CreateCandidate("standard", "Current", includeEvent: true, includeSkills: true, writerDeclared: true);
        var historical = files.CreateCandidate("historical", "Historical", includeEvent: true, includeSkills: true, writerDeclared: true)
            with { IsCurrent = false };
        var discovered = new ConcurrentQueue<EventPipelineSourceStatus>();
        using var skills = files.CreateSkillsMonitor([current, historical]);
        await using var events = files.CreateEventPipeline([current, historical]);
        events.SourceDiscovered += discovered.Enqueue;

        skills.Start();
        events.Start();
        skills.Poll();

        Assert.Equal("Current", Assert.Single(skills.GetSnapshots()).ProfileName);
        Assert.Equal("Current", Assert.Single(discovered).ProfileName);

        await events.StopAsync();
    }

    [Fact]
    public async Task NewEventFileInApprovedLogsDirectoryIsDiscoveredAfterStartupWithoutRootDiscovery()
    {
        using var files = new SourceFixture();
        var candidate = files.CreateCandidate("standard", "Alice", includeEvent: false, includeSkills: true, writerDeclared: true);
        var clock = new MutableTimeProvider();
        var discovered = new ConcurrentQueue<EventPipelineSourceStatus>();
        await using var events = files.CreateEventPipeline([candidate], clock);
        events.SourceDiscovered += discovered.Enqueue;

        events.Start();
        Assert.Empty(discovered);
        files.CreateEventFile(candidate);
        clock.Advance(TimeSpan.FromSeconds(1));
        events.RunCycleForTest();

        var source = Assert.Single(discovered);
        Assert.Equal("Alice", source.ProfileName);
        Assert.Single(events.SourceCheckpoints);

        await events.StopAsync();
    }

    [Fact]
    public async Task FailedCandidateRegistrationDoesNotBlockHealthyCandidate()
    {
        using var files = new SourceFixture();
        var healthy = files.CreateCandidate("standard", "Healthy", includeEvent: true, includeSkills: true, writerDeclared: true);
        var broken = files.CreateCandidate("broken", "Broken", includeEvent: true, includeSkills: false, writerDeclared: false);
        Directory.Delete(broken.ProfileDirectoryPath, recursive: true);
        var discovered = new ConcurrentQueue<EventPipelineSourceStatus>();
        using var skills = files.CreateSkillsMonitor([broken, healthy]);
        await using var events = files.CreateEventPipeline([broken, healthy]);
        events.SourceDiscovered += discovered.Enqueue;

        skills.Start();
        events.Start();

        Assert.Equal("Healthy", Assert.Single(skills.GetSnapshots()).ProfileName);
        Assert.Equal("Healthy", Assert.Single(discovered).ProfileName);

        await events.StopAsync();
    }

    private sealed class SourceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WurmMiningCounter-SourceCandidates-" + Guid.NewGuid().ToString("N"));

        public SourceFixture()
        {
            Directory.CreateDirectory(_root);
            Resolver = new PlayerProfileIdentityResolver(Path.Combine(_root, "identity-registry"));
        }

        public PlayerProfileIdentityResolver Resolver { get; }

        public LogDiscoverySourceCandidate CreateCandidate(string installationSegment, string profileName, bool includeEvent,
            bool includeSkills, bool writerDeclared, PlayerProfileId? informationalProfileId = null)
        {
            var root = Path.Combine(_root, installationSegment);
            var players = Path.Combine(root, "players");
            var profile = Path.Combine(players, profileName);
            var logs = Path.Combine(profile, "logs");
            Directory.CreateDirectory(logs);
            var eventFiles = new List<WurmLogFileLocation>();
            var skillFiles = new List<WurmLogFileLocation>();
            if (includeEvent)
            {
                var path = Path.Combine(logs, $"_Event.{DateTime.Now:yyyy-MM}.txt");
                File.WriteAllText(path, string.Empty);
                eventFiles.Add(new WurmLogFileLocation(WurmLogFileKind.Event, path, players, profileName, logs));
            }
            if (includeSkills)
            {
                var path = Path.Combine(logs, "_Skills.2026-10.txt");
                File.WriteAllText(path, string.Empty);
                skillFiles.Add(new WurmLogFileLocation(WurmLogFileKind.Skills, path, players, profileName, logs));
                if (writerDeclared)
                    File.WriteAllText(Path.Combine(root, $"console.{profileName}.log"), $"[10:00:00] Writing to {path}\n");
            }

            return new LogDiscoverySourceCandidate(profileName, profile, logs, eventFiles, skillFiles, [],
                informationalProfileId, true, Path.Combine(root, $"console.{profileName}.log"),
                "Active Skills writer still requires a valid client console declaration.");
        }

        public LiveSkillMonitor CreateSkillsMonitor(IReadOnlyList<LogDiscoverySourceCandidate> candidates) => new(
            Path.Combine(_root, "legacy", "players"), TimeSpan.FromDays(1), profileIdentityResolver: Resolver,
            sourceCandidates: candidates);

        public EventPipeline CreateEventPipeline(IReadOnlyList<LogDiscoverySourceCandidate> candidates,
            TimeProvider? timeProvider = null) => new(
            Path.Combine(_root, "legacy", "players"), _ => EventPipelineHandoffResult.Complete,
            new EventLogReader(), timeProvider ?? TimeProvider.System, profileIdentityResolver: Resolver, sourceCandidates: candidates);

        public void CreateEventFile(LogDiscoverySourceCandidate candidate)
        {
            File.WriteAllText(Path.Combine(candidate.LogsDirectoryPath, $"_Event.{DateTime.Now:yyyy-MM}.txt"), string.Empty);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Now;
        private long _timestamp;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }
}
