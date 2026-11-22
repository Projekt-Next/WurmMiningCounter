using System.Collections.Concurrent;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class AppRetirementIntegrationTests
{
    [Theory]
    [InlineData("ambiguity")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task UncertainDiscoveryCannotAuthorizeRetirementButLaterCompleteScanCan(string uncertainty)
    {
        using var fixture = new Fixture();
        var players = fixture.Players;
        var alice = Path.Combine(players, "Alice");
        var bob = Path.Combine(players, "Bob");
        var charlie = Path.Combine(players, "Charlie");
        var aliceLogs = Path.Combine(alice, "logs");
        var bobLogs = Path.Combine(bob, "logs");
        Directory.CreateDirectory(bobLogs);
        Directory.CreateDirectory(Path.Combine(charlie, "logs"));
        var includeAlice = true;
        var ambiguous = false;
        var uncertainIdentity = false;
        var locator = new WurmLogLocationLocator(Directory.Exists,
            path => path == players ? includeAlice ? [alice, bob, charlie] : [bob, charlie] : Directory.EnumerateDirectories(path),
            path => ambiguous && path == bobLogs ? aliceLogs : path,
            new DelegateDirectoryPhysicalIdentityProvider(path => path == alice ? Fixture.Key
                : path == bob && uncertainIdentity ? uncertainty == "missing" ? null : "physical:charlie"
                : path == bob ? "physical:bob" : path == charlie ? "physical:charlie" : "identity:" + path),
            () => players, _ => [], _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            Directory.EnumerateFiles, path => Directory.Exists(path) ? WurmLogDirectoryProbeResult.Exists : WurmLogDirectoryProbeResult.Missing);
        var discovery = new LogDiscoverySnapshotService(locator, fixture.Resolver);
        var location = new KnownLogLocation { Id = players, Enabled = true, Game = "Test", Type = KnownLogLocations.DirectType, Path = players };
        var first = discovery.Discover([location], []);
        var initial = Assert.Single(first.Reconciliation.Desired, item => item.PhysicalDirectoryKey == Fixture.Key);
        await using var pipeline = fixture.CreatePipeline((_, _) => Task.CompletedTask);
        using var monitor = fixture.CreateMonitor();
        var retirements = new Dictionary<string, App.SnapshotRetirementState>(StringComparer.Ordinal);
        pipeline.Start();
        monitor.Start();

        LogDiscoverySuppressionIntent? pending = null;
        if (uncertainty == "ambiguity")
            ambiguous = true;
        else
        {
            includeAlice = false;
            var absent = discovery.Discover([location], []);
            pending = Assert.Single(absent.Reconciliation.SuppressionIntents, item => item.PhysicalDirectoryKey == Fixture.Key);
            uncertainIdentity = true;
        }
        var uncertain = discovery.Discover([location], []);
        App.ReconcileDiscoverySnapshot(discovery.GetCurrentSnapshot()!, pipeline, monitor, retirements);
        pipeline.RunCycleForTest();
        Assert.Single(pipeline.SourceCheckpoints);
        Assert.Equal(LiveSkillSourceState.Observing, Assert.Single(monitor.GetSnapshots()).SourceState);
        Assert.Empty(retirements);
        if (uncertainty == "ambiguity")
        {
            Assert.NotEmpty(uncertain.Discovery.SourceOwnershipAmbiguities);
            Assert.Contains(uncertain.Reconciliation.Desired, item => item.PhysicalDirectoryKey == Fixture.Key && item.Generation == initial.Generation);
            Assert.DoesNotContain(uncertain.Reconciliation.SuppressionIntents, item => item.PhysicalDirectoryKey == Fixture.Key);
        }
        else
            Assert.Contains(uncertain.Reconciliation.SuppressionIntents, item => item == pending);
        Assert.False(uncertain.Reconciliation.IsComplete);

        ambiguous = false;
        uncertainIdentity = false;
        includeAlice = false;
        var confirmed = discovery.Discover([location], []);
        Assert.True(confirmed.Reconciliation.IsComplete);
        var intent = Assert.Single(confirmed.Reconciliation.SuppressionIntents, item => item.PhysicalDirectoryKey == Fixture.Key);
        Assert.Equal(initial.Generation, intent.Generation);
        if (pending is not null) Assert.Equal(pending, intent);
        App.ReconcileDiscoverySnapshot(discovery.GetCurrentSnapshot()!, pipeline, monitor, retirements);
        pipeline.RunCycleForTest();
        Assert.Empty(pipeline.SourceCheckpoints);
        Assert.Equal(LiveSkillSourceState.Incomplete, Assert.Single(monitor.GetSnapshots()).SourceState);
        Assert.Contains(Fixture.Key, retirements.Keys);
    }

    [Fact]
    public async Task CompleteIntentRetiresSkillsAndEventAndQueuesReappearanceUntilDrainCompletes()
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline = fixture.CreatePipeline((_, _) => release.Task);
        using var monitor = fixture.CreateMonitor();
        var retirements = new Dictionary<string, App.SnapshotRetirementState>(StringComparer.Ordinal);

        pipeline.Start();
        monitor.Start();

        App.ReconcileDiscoverySnapshot(fixture.Publication(2, [], [],
            [new LogDiscoverySuppressionIntent(Fixture.Key, 1, 2)]), pipeline, monitor, retirements);
        pipeline.RunCycleForTest();

        Assert.Empty(pipeline.SourceCheckpoints);
        Assert.Equal(LiveSkillSourceState.Incomplete, Assert.Single(monitor.GetSnapshots()).SourceState);

        App.ReconcileDiscoverySnapshot(fixture.Publication(3, [fixture.Candidate],
            [new LogDiscoveryCandidateState(Fixture.Key, 2, fixture.Candidate)], []), pipeline, monitor, retirements);
        pipeline.RunCycleForTest();
        Assert.Empty(pipeline.SourceCheckpoints);

        release.TrySetResult();
        pipeline.RunCycleForTest();
        pipeline.DiscoverForTest();
        pipeline.RunCycleForTest();

        Assert.Single(pipeline.SourceCheckpoints);
        Assert.NotEqual(LiveSkillSourceState.Observing, Assert.Single(monitor.GetSnapshots()).SourceState);
    }

    [Fact]
    public async Task IncompleteSnapshotKeepsAddOnlyRefreshAndDoesNotRetire()
    {
        using var fixture = new Fixture();
        await using var pipeline = fixture.CreatePipeline((_, _) => Task.CompletedTask);
        using var monitor = fixture.CreateMonitor();
        var retirements = new Dictionary<string, App.SnapshotRetirementState>(StringComparer.Ordinal);

        pipeline.Start();
        monitor.Start();
        var snapshot = fixture.Publication(2, [], [],
            [new LogDiscoverySuppressionIntent(Fixture.Key, 1, 2)], isComplete: false);

        App.ReconcileDiscoverySnapshot(snapshot, pipeline, monitor, retirements);
        pipeline.RunCycleForTest();

        Assert.Single(pipeline.SourceCheckpoints);
        Assert.Equal(LiveSkillSourceState.Observing, Assert.Single(monitor.GetSnapshots()).SourceState);
        Assert.Empty(retirements);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Key = "physical:alice";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WurmAppRetirement-" + Guid.NewGuid().ToString("N"));
        private readonly string _profile;
        private readonly string _logs;
        private readonly string _console;
        private readonly PlayerProfileIdentityResolver _resolver;

        public Fixture()
        {
            _profile = Path.Combine(_root, "players", "Alice");
            _logs = Path.Combine(_profile, "logs");
            _console = Path.Combine(_root, "console.Alice.log");
            Directory.CreateDirectory(_logs);
            File.WriteAllText(Path.Combine(_logs, "_Event.2026-10.txt"), "history\n");
            var skills = Path.Combine(_logs, "_Skills.2026-10.txt");
            File.WriteAllText(skills, "Mining increased to 80.000\n");
            File.WriteAllText(_console, $"[10:00:00] Writing to {skills}\n");
            _resolver = new PlayerProfileIdentityResolver(Path.Combine(_root, "registry"),
                new DelegateDirectoryPhysicalIdentityProvider(path =>
                    string.Equals(path, _profile, StringComparison.OrdinalIgnoreCase) ? Key : "identity:" + path));
            Candidate = new LogDiscoverySourceCandidate("Alice", _profile, _logs, [], [], [], null, true, _console, "test")
            { PhysicalDirectoryKey = Key };
        }

        public LogDiscoverySourceCandidate Candidate { get; }
        public string Players => Path.Combine(_root, "players");
        public PlayerProfileIdentityResolver Resolver => _resolver;

        public EventPipeline CreatePipeline(Func<PlayerProfileId, string, Task> release) => new(
            Path.Combine(_root, "players"), _ => EventPipelineHandoffResult.Complete, new EventLogReader(),
            new ControlledEventRetirementTests.ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)),
            profileIdentityResolver: _resolver, sourceCandidates: [Candidate], releaseRetiredProfileAsync: release);

        public LiveSkillMonitor CreateMonitor() => new(Path.Combine(_root, "players"), TimeSpan.FromDays(1),
            profileIdentityResolver: _resolver, sourceCandidates: [Candidate]);

        public LogDiscoverySnapshotPublication Publication(long version,
            IReadOnlyList<LogDiscoverySourceCandidate> candidates,
            IReadOnlyList<LogDiscoveryCandidateState> current,
            IReadOnlyList<LogDiscoverySuppressionIntent> intents,
            bool isComplete = true)
        {
            var plan = new LogDiscoveryReconciliationPlan(version, isComplete, current, current, intents);
            var discovery = new WurmLogLocationResult([], [], [], [], []) { IsComplete = isComplete };
            return new LogDiscoverySnapshotPublication(version,
                new LogDiscoverySnapshot(discovery, [], candidates, plan));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
