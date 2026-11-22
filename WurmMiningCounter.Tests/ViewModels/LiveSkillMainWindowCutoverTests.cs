using System.Text;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class LiveSkillMainWindowCutoverTests
{
    [Fact]
    public void InitialSnapshot_ShowsBootstrapMiningCurrentWithZeroSessionGain()
    {
        using var fixture = StartedFixture();
        var viewModel = new MainViewModel(CounterWithProfile(fixture, "Alice"), liveSkillMonitor: fixture.Monitor, postToUi: action => action());

        var panel = Assert.Single(viewModel.ProfilePanels);
        Assert.Equal("80", panel.LiveMiningCurrentDisplay);
        Assert.Equal("0", panel.LiveMiningGainDisplay);
    }

    [Fact]
    public void ProfileChanged_UpdatesMiningWithoutRefreshThroughPostToUi()
    {
        using var fixture = StartedFixture();
        var scheduledActions = new List<Action>();
        var viewModel = new MainViewModel(CounterWithProfile(fixture, "Alice"), liveSkillMonitor: fixture.Monitor, postToUi: scheduledActions.Add);

        fixture.AppendSource("Alice", "_Skills.2026-10.txt", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();

        Assert.Single(scheduledActions);
        viewModel.Refresh();
        Assert.Equal("80", Assert.Single(viewModel.ProfilePanels).LiveMiningCurrentDisplay);
        scheduledActions.Single()();
        var panel = Assert.Single(viewModel.ProfilePanels);
        Assert.Equal("80.001", panel.LiveMiningCurrentDisplay);
        Assert.Equal("+0.001", panel.LiveMiningGainDisplay);
    }

    [Fact]
    public void DecreaseUpdatesCurrentButPreservesSessionGainAndNonMiningDoesNotChangeMiningDisplay()
    {
        using var fixture = StartedFixture();
        var viewModel = new MainViewModel(CounterWithProfile(fixture, "Alice"), liveSkillMonitor: fixture.Monitor, postToUi: action => action());

        fixture.AppendSource("Alice", "_Skills.2026-10.txt", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();
        fixture.AppendSource("Alice", "_Skills.2026-10.txt", DecreaseLine("Mining", 80.000m) + SkillLine("Prospecting", 10m));
        fixture.Monitor.Poll();

        var panel = Assert.Single(viewModel.ProfilePanels);
        Assert.Equal("80", panel.LiveMiningCurrentDisplay);
        Assert.Equal("+0.001", panel.LiveMiningGainDisplay);
    }

    [Fact]
    public void Detach_IgnoresQueuedAndFutureLiveSkillUpdates()
    {
        using var fixture = StartedFixture();
        var scheduledActions = new List<Action>();
        var viewModel = new MainViewModel(CounterWithProfile(fixture, "Alice"), liveSkillMonitor: fixture.Monitor, postToUi: scheduledActions.Add);

        fixture.AppendSource("Alice", "_Skills.2026-10.txt", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();
        viewModel.DetachLiveSkillUpdates();
        viewModel.DetachLiveSkillUpdates();
        Assert.Single(scheduledActions)();

        fixture.AppendSource("Alice", "_Skills.2026-10.txt", SkillLine("Mining", 80.002m));
        fixture.Monitor.Poll();

        Assert.Equal("80", Assert.Single(viewModel.ProfilePanels).LiveMiningCurrentDisplay);
        Assert.Single(scheduledActions);
    }

    [Fact]
    public void LiveSkillOnlyProfile_DoesNotCreatePanel()
    {
        using var fixture = StartedFixture();
        var viewModel = new MainViewModel(new MiningSessionCounter(), liveSkillMonitor: fixture.Monitor, postToUi: action => action());

        Assert.Empty(viewModel.ProfilePanels);
    }

    [Fact]
    public void ProfileChanged_UpdatesOnlyTheChangedProfilesLiveSkillState()
    {
        using var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", SkillLine("Mining", 80m));
        fixture.CreateSource("Bob", "_Skills.2026-10.txt", SkillLine("Mining", 70m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
        fixture.AppendConsole("Bob", fixture.SourcePath("Bob", "_Skills.2026-10.txt"));
        fixture.Monitor.Start();
        var counter = CounterWithProfile(fixture, "Alice");
        counter.Add(fixture.ProfileId("Bob"), "Bob", new MiningEvent(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter, liveSkillMonitor: fixture.Monitor, postToUi: action => action());

        fixture.AppendSource("Alice", "_Skills.2026-10.txt", SkillLine("Mining", 80.001m));
        fixture.Monitor.Poll();

        Assert.Equal("80.001", viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Alice").LiveMiningCurrentDisplay);
        Assert.Equal("70", viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Bob").LiveMiningCurrentDisplay);
    }

    private static MiningSessionCounter CounterWithProfile(Fixture fixture, string profileName)
    {
        var counter = new MiningSessionCounter();
        counter.Add(fixture.ProfileId(profileName), profileName,
            new MiningEvent(MiningMaterial.Ore));
        return counter;
    }

    private static Fixture StartedFixture()
    {
        var fixture = new Fixture();
        fixture.CreateSource("Alice", "_Skills.2026-10.txt", SkillLine("Mining", 80m));
        fixture.AppendConsole("Alice", fixture.SourcePath("Alice", "_Skills.2026-10.txt"));
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
            Monitor = new LiveSkillMonitor(Path.Combine(Root, "players"), TimeSpan.FromDays(1),
                profileIdentityResolver: new PlayerProfileIdentityResolver(Path.Combine(Root, "identity-registry")));
        }

        public string Root { get; }

        public LiveSkillMonitor Monitor { get; }

        public PlayerProfileId ProfileId(string profileName) => Monitor.GetSnapshots()
            .Single(snapshot => snapshot.ProfileName == profileName).ProfileId;

        public string SourcePath(string profile, string fileName) => Path.Combine(Root, "players", profile, "logs", fileName);

        public void CreateSource(string profile, string fileName, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath(profile, fileName))!);
            File.WriteAllText(SourcePath(profile, fileName), text, Utf8);
        }

        public void AppendSource(string profile, string fileName, string text) =>
            File.AppendAllText(SourcePath(profile, fileName), text, Utf8);

        public void AppendConsole(string profile, string sourcePath) =>
            File.AppendAllText(Path.Combine(Root, $"console.{profile}.log"), $"[10:00:00] Writing to {sourcePath}\n", Utf8);

        public void Dispose()
        {
            Monitor.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
