using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.MiningStatistics;

public sealed class LiveSkillRetirementTests
{
    [Fact]
    public void ActiveRetirementRetainsStatisticsAndReappearanceRebindsOnlyAfterNewWriterDeclaration()
    {
        using var fixture = new CandidateFixture();
        fixture.WriteSkills("_Skills.2026-10.txt", "Mining increased to 80.000\n");
        fixture.DeclareWriter("_Skills.2026-10.txt");
        using var monitor = fixture.CreateMonitor();

        monitor.Start();
        var before = Assert.Single(monitor.GetSnapshots());
        Assert.True(monitor.TryRetireSource(CandidateFixture.Key, 1, 1));
        Assert.True(monitor.TryRetireSource(CandidateFixture.Key, 1, 1));

        var retired = Assert.Single(monitor.GetSnapshots());
        Assert.Equal(before.ProfileId, retired.ProfileId);
        Assert.Equal(before.Skills, retired.Skills);
        Assert.Null(retired.ActiveSourcePath);
        Assert.Equal(LiveSkillSourceState.Incomplete, retired.SourceState);
        Assert.Empty(fixture.Resolver.GetActivationStatusSnapshot());

        Assert.True(monitor.TryPermitReappearance(CandidateFixture.Key, 1, 2, 2));
        monitor.Poll(); // Existing console declaration remains before the retained tail checkpoint.
        Assert.Null(Assert.Single(monitor.GetSnapshots()).ActiveSourcePath);

        fixture.WriteSkills("_Skills.2026-11.txt", string.Empty);
        fixture.DeclareWriter("_Skills.2026-11.txt");
        monitor.Poll();

        var rebound = Assert.Single(monitor.GetSnapshots());
        Assert.Equal(before.ProfileId, rebound.ProfileId);
        Assert.Single(monitor.GetSnapshots());
        Assert.Equal(fixture.SkillsPath("_Skills.2026-11.txt"), rebound.ActiveSourcePath);
        Assert.Equal(LiveSkillSourceState.Observing, rebound.SourceState);
        Assert.Equal(before.Skills, rebound.Skills);
    }

    [Fact]
    public void PendingRetirementReleasesRegistrationAndCannotActivateUntilReappearance()
    {
        using var fixture = new CandidateFixture();
        fixture.WriteSkills("_Skills.2026-10.txt", "Mining increased to 80.000\n");
        fixture.DeclareWriter("_Skills.2026-10.txt");
        var fail = true;
        using var monitor = fixture.CreateMonitor(path =>
        {
            if (fail)
                throw new IOException("expected pending registration");
            return Directory.EnumerateFiles(path, "_Skills*.txt", SearchOption.TopDirectoryOnly);
        });

        monitor.Start();
        Assert.Single(fixture.Resolver.GetActivationStatusSnapshot());
        Assert.True(monitor.TryRetireSource(CandidateFixture.Key, 1, 1));
        Assert.Empty(fixture.Resolver.GetActivationStatusSnapshot());
        fail = false;
        monitor.Poll();
        Assert.Empty(monitor.GetSnapshots());

        Assert.True(monitor.TryPermitReappearance(CandidateFixture.Key, 1, 2, 2));
        monitor.Poll();
        Assert.Single(monitor.GetSnapshots());
    }

    [Fact]
    public void MissingConflictingAndStaleRetirementRequestsAreFailClosed()
    {
        using var fixture = new CandidateFixture(duplicateCandidate: true);
        fixture.WriteSkills("_Skills.2026-10.txt", "Mining increased to 80.000\n");
        fixture.DeclareWriter("_Skills.2026-10.txt");
        using var monitor = fixture.CreateMonitor();

        monitor.Start();
        Assert.False(monitor.TryRetireSource("missing", 1, 1));
        Assert.False(monitor.TryRetireSource(CandidateFixture.Key, 1, 1));
        Assert.False(monitor.TryPermitReappearance(CandidateFixture.Key, 1, 2, 2));
        Assert.Equal(LiveSkillSourceState.Observing, Assert.Single(monitor.GetSnapshots()).SourceState);
    }

    [Fact]
    public void StaleReappearanceGenerationCannotClearSuppression()
    {
        using var fixture = new CandidateFixture();
        fixture.WriteSkills("_Skills.2026-10.txt", string.Empty);
        fixture.DeclareWriter("_Skills.2026-10.txt");
        using var monitor = fixture.CreateMonitor();

        monitor.Start();
        Assert.True(monitor.TryRetireSource(CandidateFixture.Key, 2, 4));
        Assert.False(monitor.TryPermitReappearance(CandidateFixture.Key, 1, 3, 5));
        monitor.Poll();

        Assert.Equal(LiveSkillSourceState.Incomplete, Assert.Single(monitor.GetSnapshots()).SourceState);
    }

    [Fact]
    public void RetirementAndSameIdReappearanceSynchronizeMainViewWithoutANewGain()
    {
        using var fixture = new CandidateFixture();
        fixture.WriteSkills("_Skills.2026-10.txt", "[10:00:00] Mining increased by 0,001000 to 80,000000\n");
        fixture.DeclareWriter("_Skills.2026-10.txt");
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        var profileId = Assert.Single(monitor.GetSnapshots()).ProfileId;
        var counter = new MiningSessionCounter();
        counter.Add(profileId, "Alice", new MiningEvent(MiningMaterial.Ore));
        var queued = new List<Action>();
        var viewModel = new MainViewModel(counter, liveSkillMonitor: monitor, postToUi: queued.Add);
        Assert.Equal("80", Assert.Single(viewModel.ProfilePanels).LiveMiningCurrentDisplay);

        Assert.True(monitor.TryRetireSource(CandidateFixture.Key, 1, 1));
        var retirementCallback = Assert.Single(queued);
        queued.Clear();
        retirementCallback();
        Assert.Equal("—", Assert.Single(viewModel.ProfilePanels).LiveMiningCurrentDisplay);
        Assert.Equal(LiveSkillSourceState.Incomplete, Assert.Single(monitor.GetSnapshots()).SourceState);

        Assert.True(monitor.TryPermitReappearance(CandidateFixture.Key, 1, 2, 2));
        monitor.Poll();
        var reappearanceCallback = Assert.Single(queued);
        queued.Clear();
        reappearanceCallback();
        Assert.Equal("—", Assert.Single(viewModel.ProfilePanels).LiveMiningCurrentDisplay);
        var reboundBeforeWriter = Assert.Single(monitor.GetSnapshots());
        Assert.Equal(profileId, reboundBeforeWriter.ProfileId);
        Assert.Equal(80m, reboundBeforeWriter.Skills.Single(skill => skill.SkillName == "Mining").CurrentValue);

        fixture.WriteSkills("_Skills.2026-11.txt", string.Empty);
        fixture.DeclareWriter("_Skills.2026-11.txt");
        monitor.Poll();
        var writerCallback = Assert.Single(queued);
        queued.Clear();
        writerCallback();
        Assert.Equal("80", Assert.Single(viewModel.ProfilePanels).LiveMiningCurrentDisplay);

        File.AppendAllText(fixture.SkillsPath("_Skills.2026-11.txt"),
            "[10:00:01] Mining increased by 0,001000 to 80,001000\n");
        monitor.Poll();
        var gainCallback = Assert.Single(queued);
        queued.Clear();
        gainCallback();
        Assert.Equal("80.001", Assert.Single(viewModel.ProfilePanels).LiveMiningCurrentDisplay);
    }

    [Fact]
    public void DelayedRetirementCallbackReadsTheCurrentReboundSnapshot()
    {
        using var fixture = new CandidateFixture();
        fixture.WriteSkills("_Skills.2026-10.txt", "[10:00:00] Mining increased by 0,001000 to 80,000000\n");
        fixture.DeclareWriter("_Skills.2026-10.txt");
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        var profileId = Assert.Single(monitor.GetSnapshots()).ProfileId;
        var counter = new MiningSessionCounter();
        counter.Add(profileId, "Alice", new MiningEvent(MiningMaterial.Ore));
        var queued = new List<Action>();
        var viewModel = new MainViewModel(counter, liveSkillMonitor: monitor, postToUi: queued.Add);

        Assert.True(monitor.TryRetireSource(CandidateFixture.Key, 1, 1));
        var delayedRetirement = Assert.Single(queued);
        Assert.True(monitor.TryPermitReappearance(CandidateFixture.Key, 1, 2, 2));
        monitor.Poll();
        fixture.WriteSkills("_Skills.2026-11.txt", string.Empty);
        fixture.DeclareWriter("_Skills.2026-11.txt");
        monitor.Poll();

        delayedRetirement();

        Assert.Equal(profileId, Assert.Single(monitor.GetSnapshots()).ProfileId);
        Assert.Equal("80", Assert.Single(viewModel.ProfilePanels).LiveMiningCurrentDisplay);
    }

    [Fact]
    public void RetirementAndReappearancePreserveSkillStatisticsSelection()
    {
        using var fixture = new CandidateFixture();
        fixture.WriteSkills("_Skills.2026-10.txt", "[10:00:00] Mining increased by 0,001000 to 80,000000\n");
        fixture.DeclareWriter("_Skills.2026-10.txt");
        using var monitor = fixture.CreateMonitor();
        monitor.Start();

        var profileId = Assert.Single(monitor.GetSnapshots()).ProfileId;
        var counter = new MiningSessionCounter();
        counter.Add(profileId, "Alice", new MiningEvent(MiningMaterial.Ore));
        var statistics = new SkillStatisticsViewModel(monitor, counter, action => action(),
            sessionBaselines: new MiningStatisticsSessionBaselineStore());
        statistics.SelectedProfileId = profileId;

        Assert.True(monitor.TryRetireSource(CandidateFixture.Key, 1, 1));
        Assert.Equal(profileId, statistics.SelectedProfileId);
        Assert.Equal(profileId, Assert.Single(statistics.Profiles).ProfileId);

        Assert.True(monitor.TryPermitReappearance(CandidateFixture.Key, 1, 2, 2));
        monitor.Poll();
        Assert.Equal(profileId, statistics.SelectedProfileId);
        Assert.Equal(profileId, Assert.Single(statistics.Profiles).ProfileId);
    }

    private sealed class CandidateFixture : IDisposable
    {
        internal const string Key = "physical:alice";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WurmMiningCounter-Retirement-" + Guid.NewGuid().ToString("N"));
        private readonly string _profile;
        private readonly string _logs;
        private readonly string _console;

        public CandidateFixture(bool duplicateCandidate = false)
        {
            _profile = Path.Combine(_root, "players", "Alice");
            _logs = Path.Combine(_profile, "logs");
            _console = Path.Combine(_root, "console.Alice.log");
            Directory.CreateDirectory(_logs);
            Resolver = new PlayerProfileIdentityResolver(Path.Combine(_root, "registry"),
                new DelegateDirectoryPhysicalIdentityProvider(path => string.Equals(path, _profile, StringComparison.OrdinalIgnoreCase)
                    ? Key : "identity:" + path));
            Candidate = new LogDiscoverySourceCandidate("Alice", _profile, _logs, [], [], [], null, true, _console, "test")
            { PhysicalDirectoryKey = Key };
            Candidates = duplicateCandidate ? [Candidate, Candidate with { ProfileDirectoryPath = Path.Combine(_root, "alias", "Alice") }] : [Candidate];
        }

        public PlayerProfileIdentityResolver Resolver { get; }
        public LogDiscoverySourceCandidate Candidate { get; }
        public IReadOnlyList<LogDiscoverySourceCandidate> Candidates { get; }

        public LiveSkillMonitor CreateMonitor(Func<string, IEnumerable<string>>? enumerate = null) => new(
            Path.Combine(_root, "unused-players"), TimeSpan.FromDays(1), profileIdentityResolver: Resolver,
            enumerateSkillFiles: enumerate, sourceCandidates: Candidates);

        public string SkillsPath(string name) => Path.Combine(_logs, name);
        public void WriteSkills(string name, string content) => File.WriteAllText(SkillsPath(name), content);
        public void DeclareWriter(string name) => File.AppendAllText(_console, $"[10:00:00] Writing to {SkillsPath(name)}\n");

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }
}
