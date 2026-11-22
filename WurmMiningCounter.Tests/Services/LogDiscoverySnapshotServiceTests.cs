using System.Diagnostics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class LogDiscoverySnapshotServiceTests
{
    [Fact]
    public void RealJunctionLogsRetainProfileIdentityAndOwnershipAcrossSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-junction-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var players = Path.Combine(root, "players");
            var alice = Path.Combine(players, "Alice");
            var logs = Path.Combine(alice, "logs");
            var target = Path.Combine(root, "external", "logs");
            Directory.CreateDirectory(alice);
            Directory.CreateDirectory(target);
            var eventFile = Path.Combine(target, "_Event.2026-10.txt");
            var skillsFile = Path.Combine(target, "_Skills.2026-10.txt");
            File.WriteAllText(eventFile, "historical\n");
            File.WriteAllText(skillsFile, "historical\n");
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "/c", "mklink", "/J", logs, target })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)!;
            Assert.True(process.WaitForExit(5000));
            Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());

            var provider = new WindowsDirectoryPhysicalIdentityProvider();
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var service = new LogDiscoverySnapshotService(new WurmLogLocationLocator(), resolver);
            var first = service.Discover([Location(players)], []);
            Assert.True(first.IsComplete);
            var profile = Assert.Single(first.Profiles);
            var candidate = Assert.Single(first.SourceCandidates);
            Assert.Equal(alice, Assert.Single(Assert.Single(first.Discovery.DataRoots).Profiles).ProfileDirectoryPath);
            Assert.Equal(alice, profile.ProfileDirectoryPath);
            Assert.Equal(target, profile.LogsDirectoryPath);
            Assert.Equal(alice, candidate.ProfileDirectoryPath);
            Assert.Equal(target, candidate.LogsDirectoryPath);
            Assert.NotNull(profile.ProfileId);
            Assert.Equal(profile.ProfileId, candidate.InformationalProfileId);
            var physicalKey = provider.GetIdentity(alice);
            Assert.NotNull(physicalKey);
            Assert.Equal(physicalKey, candidate.PhysicalDirectoryKey);
            Assert.Equal(eventFile, Assert.Single(candidate.EventFiles).FilePath);
            Assert.Equal(skillsFile, Assert.Single(candidate.SkillsFiles).FilePath);
            Assert.Equal(Path.Combine(root, "console.Alice.log"), candidate.ConsolePath);
            Assert.True(resolver.TryRegisterSource(candidate.ProfileDirectoryPath, out var owner, out var initialGuard));
            using var registration = owner!;
            initialGuard!.Dispose();

            var second = service.Discover([Location(players)], []);
            Assert.True(second.IsComplete);
            var refreshed = Assert.Single(second.SourceCandidates);
            Assert.Equal(profile.ProfileId, Assert.Single(second.Profiles).ProfileId);
            Assert.Equal(physicalKey, refreshed.PhysicalDirectoryKey);
            Assert.Equal(alice, refreshed.ProfileDirectoryPath);
            Assert.Equal(target, refreshed.LogsDirectoryPath);
            Assert.Equal(1, Assert.Single(second.Reconciliation.Current).Generation);
            Assert.Empty(second.Reconciliation.SuppressionIntents);
            Assert.Equal(ProfileSourceOwnershipStatus.Verified, registration.TryOpenRead(out var guard));
            guard!.Dispose();
        }
        finally
        {
            var junction = Path.Combine(root, "players", "Alice", "logs");
            if (Directory.Exists(junction)) Directory.Delete(junction);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LinkedLogsKeepProfileOwnersAcrossRefreshAndDetectRealReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-linked-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var players = Path.Combine(root, "players");
            var alice = Path.Combine(players, "Alice");
            var bob = Path.Combine(players, "Bob");
            var carol = Path.Combine(players, "Carol");
            var aliceLogs = Path.Combine(alice, "logs");
            var bobLogs = Path.Combine(bob, "logs");
            var carolLogs = Path.Combine(carol, "logs");
            var aliceTarget = Path.Combine(root, "external", "alice-logs");
            var bobTarget = Path.Combine(root, "external", "bob-logs");
            foreach (var directory in new[] { aliceLogs, bobLogs, carolLogs, aliceTarget, bobTarget })
                Directory.CreateDirectory(directory);
            foreach (var directory in new[] { aliceTarget, bobTarget, carolLogs })
            {
                File.WriteAllText(Path.Combine(directory, "_Event.2026-10.txt"), "historical\n");
                File.WriteAllText(Path.Combine(directory, "_Skills.2026-10.txt"), "historical\n");
            }
            var provider = new WindowsDirectoryPhysicalIdentityProvider();
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var locator = new WurmLogLocationLocator(Directory.Exists, Directory.EnumerateDirectories,
                path => path == aliceLogs ? aliceTarget : path == bobLogs ? bobTarget : path,
                provider, () => players, _ => [],
                _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
                Directory.EnumerateFiles);
            var service = new LogDiscoverySnapshotService(locator, resolver);
            var first = service.Discover([Location(players)], []);
            Assert.True(first.IsComplete);
            Assert.Equal(3, first.Profiles.Count);
            foreach (var (name, profilePath, logsPath) in new[]
                     { ("Alice", alice, aliceTarget), ("Bob", bob, bobTarget), ("Carol", carol, carolLogs) })
            {
                var profile = Assert.Single(first.Profiles, p => p.ProfileName == name);
                var candidate = Assert.Single(first.SourceCandidates, p => p.DisplayName == name);
                Assert.Equal(profilePath, profile.ProfileDirectoryPath);
                Assert.Equal(logsPath, profile.LogsDirectoryPath);
                Assert.NotNull(profile.ProfileId);
                Assert.Equal(profilePath, candidate.ProfileDirectoryPath);
                Assert.Equal(provider.GetIdentity(profilePath), candidate.PhysicalDirectoryKey);
                Assert.NotNull(candidate.PhysicalDirectoryKey);
                Assert.Equal(Path.Combine(logsPath, "_Event.2026-10.txt"), Assert.Single(candidate.EventFiles).FilePath);
                Assert.Equal(Path.Combine(logsPath, "_Skills.2026-10.txt"), Assert.Single(candidate.SkillsFiles).FilePath);
                Assert.Equal(Path.Combine(root, $"console.{name}.log"), candidate.ConsolePath);
            }
            var aliceKey = provider.GetIdentity(alice);
            var bobKey = provider.GetIdentity(bob);
            Assert.NotEqual(aliceKey, bobKey);
            Assert.True(resolver.TryRegisterSource(alice, out var aliceOwner, out var aliceGuard));
            using var registration = aliceOwner!;
            aliceGuard!.Dispose();
            var second = service.Discover([Location(players)], []);
            Assert.True(second.IsComplete);
            Assert.Empty(second.Reconciliation.SuppressionIntents);
            Assert.Equal(3, second.Reconciliation.Current.Count);
            Assert.All(second.Reconciliation.Current, state => Assert.Equal(1, state.Generation));
            Assert.Equal(first.Profiles.Single(p => p.ProfileName == "Alice").ProfileId,
                second.Profiles.Single(p => p.ProfileName == "Alice").ProfileId);
            Assert.Equal(ProfileSourceOwnershipStatus.Verified, registration.TryOpenRead(out var guard));
            guard!.Dispose();

            Directory.Move(alice, Path.Combine(root, "alice-old"));
            Directory.CreateDirectory(aliceLogs);
            var replacement = service.Discover([Location(players)], []);
            Assert.True(replacement.IsComplete);
            var newKey = Assert.Single(replacement.SourceCandidates, p => p.DisplayName == "Alice").PhysicalDirectoryKey;
            Assert.NotNull(newKey);
            Assert.NotEqual(aliceKey, newKey);
            Assert.Equal(aliceKey, Assert.Single(replacement.Reconciliation.SuppressionIntents).PhysicalDirectoryKey);
            Assert.Contains(replacement.Reconciliation.Current, state => state.PhysicalDirectoryKey == newKey);
            Assert.Equal(ProfileSourceOwnershipStatus.Changed, registration.TryOpenRead(out var replacementGuard));
            Assert.Null(replacementGuard);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingPhysicalIdentityKeepsFilesButDoesNotCreateIdOrLease()
    {
        const string players = "C:\\Data\\players";
        const string profile = "C:\\Data\\players\\Alice";
        const string logs = "C:\\Data\\players\\Alice\\logs";
        using var fixture = new RegistryFixture();
        var service = new LogDiscoverySnapshotService(CreateLocator(players, profile, logs, _ => null),
            fixture.CreateResolver(_ => null));

        var snapshot = service.Discover([Location(players)], []);

        var discovered = Assert.Single(snapshot.Profiles);
        Assert.Null(discovered.ProfileId);
        Assert.NotNull(discovered.IdentityDiagnostic);
        Assert.Equal(2, discovered.Files.Count);
        Assert.Empty(fixture.Resolver.GetActivationStatusSnapshot());
        var candidate = Assert.Single(snapshot.SourceCandidates);
        Assert.Null(candidate.InformationalProfileId);
        Assert.True(candidate.IsEligibleForFutureActivation);
        Assert.Equal("C:\\Data\\console.Alice.log", candidate.ConsolePath);
        Assert.Contains("valid client console declaration", candidate.ConsoleWriterEvidence);
    }

    [Fact]
    public void AliasUsesOneProfileAndRetainsBothOrigins()
    {
        const string target = "C:\\Target\\players";
        const string alias = "C:\\Alias\\players";
        const string profile = "C:\\Target\\players\\Same";
        using var fixture = new RegistryFixture();
        var locator = new WurmLogLocationLocator(_ => true, path => path == target ? [profile] : [],
            path => path == alias ? target : path,
            new DelegateDirectoryPhysicalIdentityProvider(_ => "physical:target"), () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [], _ => WurmLogDirectoryProbeResult.Exists);
        var service = new LogDiscoverySnapshotService(locator, fixture.CreateResolver(_ => "profile-id"));

        var snapshot = service.Discover([Location(target, "target"), Location(alias, "alias")], []);

        Assert.Single(snapshot.Profiles);
        Assert.Equal(2, Assert.Single(snapshot.Discovery.DataRoots).Origins.Count);
        Assert.Equal(2, Assert.Single(snapshot.SourceCandidates).Origins.Count);
    }

    [Fact]
    public void PartialSnapshotRetainsOnlyUntilNextCompleteScan()
    {
        const string first = "C:\\One\\players";
        const string second = "C:\\Two\\players";
        const string firstProfile = "C:\\One\\players\\Alice";
        const string secondProfile = "C:\\Two\\players\\Bob";
        var secondAvailable = true;
        using var fixture = new RegistryFixture();
        var locator = new WurmLogLocationLocator(_ => true,
            path => path == first ? [firstProfile] : path == second && secondAvailable ? [secondProfile] : [],
            path => path, new DelegateDirectoryPhysicalIdentityProvider(path => "id:" + path), () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [], path => path == second && !secondAvailable ? WurmLogDirectoryProbeResult.AccessDenied : WurmLogDirectoryProbeResult.Exists);
        var service = new LogDiscoverySnapshotService(locator, fixture.CreateResolver(_ => "confirmed"));
        var locations = new[] { Location(first), Location(second) };

        Assert.Equal(2, service.Discover(locations, []).Profiles.Count);
        secondAvailable = false;
        var partial = service.Discover(locations, []);
        Assert.Contains(partial.Profiles, profile => profile.ProfileName == "Alice" && profile.IsCurrent);
        Assert.Contains(partial.Profiles, profile => profile.ProfileName == "Bob" && !profile.IsCurrent);
        Assert.Contains(partial.SourceCandidates, candidate => candidate.DisplayName == "Bob" && !candidate.IsEligibleForFutureActivation);
        Assert.Empty(partial.Reconciliation.SuppressionIntents);
        Assert.Equal(2, partial.Reconciliation.Desired.Count);
        secondAvailable = true;
        var complete = service.Discover([Location(first)], []);
        Assert.Single(complete.Profiles);
        Assert.Equal("Alice", complete.Profiles[0].ProfileName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviouslyAvailableMissingRootRetainsSourceUntilReturnOrConfigurationRemoval(bool manual)
    {
        const string players = "C:\\Data\\players";
        const string profile = "C:\\Data\\players\\Alice";
        var missing = false;
        using var fixture = new RegistryFixture();
        var locator = new WurmLogLocationLocator(_ => true, path => path == players ? [profile] : [], path => path,
            new DelegateDirectoryPhysicalIdentityProvider(path => "id:" + path), () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [], path => path == players && missing ? WurmLogDirectoryProbeResult.Missing : WurmLogDirectoryProbeResult.Exists);
        var service = new LogDiscoverySnapshotService(locator, fixture.CreateResolver(path => "id:" + path));

        LogDiscoverySnapshot Discover(string path) => manual
            ? service.Discover([], [path]) : service.Discover([Location(path)], []);

        var first = Discover(players);
        var firstProfile = Assert.Single(first.Profiles);
        var firstState = Assert.Single(first.Reconciliation.Desired);
        Assert.NotNull(firstProfile.ProfileId);
        missing = true;
        for (var i = 0; i < 2; i++)
        {
            var partial = Discover(players + "\\");
            Assert.False(partial.IsComplete);
            Assert.False(partial.Reconciliation.IsComplete);
            Assert.False(Assert.Single(partial.Profiles).IsCurrent);
            Assert.Equal(firstState, Assert.Single(partial.Reconciliation.Desired));
            Assert.Empty(partial.Reconciliation.SuppressionIntents);
        }

        missing = false;
        var returned = Discover(players);
        Assert.True(returned.IsComplete);
        Assert.Equal(firstProfile.ProfileId, Assert.Single(returned.Profiles).ProfileId);
        Assert.Equal(firstState.PhysicalDirectoryKey, Assert.Single(returned.Reconciliation.Current).PhysicalDirectoryKey);
        Assert.Equal(firstState.Generation, Assert.Single(returned.Reconciliation.Current).Generation);
        Assert.Empty(returned.Reconciliation.SuppressionIntents);

        missing = true;
        Assert.False(Discover(players).Reconciliation.IsComplete);
        var removed = service.Discover([], []);
        Assert.True(removed.Reconciliation.IsComplete);
        var intent = Assert.Single(removed.Reconciliation.SuppressionIntents);
        Assert.Equal(firstState.PhysicalDirectoryKey, intent.PhysicalDirectoryKey);
        Assert.Equal(firstState.Generation, intent.Generation);
    }

    [Fact]
    public void MissingConfiguredRootMakesExistingRetirementIntentNonExecutableUntilCompleteScan()
    {
        const string players = "C:\\Data\\players";
        const string alice = "C:\\Data\\players\\Alice";
        const string bob = "C:\\Data\\players\\Bob";
        var profiles = new List<string> { alice, bob };
        var missing = false;
        using var fixture = new RegistryFixture();
        var locator = new WurmLogLocationLocator(_ => true, path => path == players ? profiles.ToArray() : [], path => path,
            new DelegateDirectoryPhysicalIdentityProvider(path => "id:" + path), () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [], path => path == players && missing ? WurmLogDirectoryProbeResult.Missing : WurmLogDirectoryProbeResult.Exists);
        var service = new LogDiscoverySnapshotService(locator, fixture.CreateResolver(path => "id:" + path));

        _ = service.Discover([Location(players)], []);
        profiles.Remove(bob);
        var complete = service.Discover([Location(players)], []);
        var pending = Assert.Single(complete.Reconciliation.SuppressionIntents);
        Assert.Equal("id:" + bob, pending.PhysicalDirectoryKey);
        missing = true;
        var partial = service.Discover([Location(players)], []);
        Assert.False(partial.IsComplete);
        Assert.False(partial.Reconciliation.IsComplete);
        Assert.Equal(pending, Assert.Single(partial.Reconciliation.SuppressionIntents));
        Assert.Equal("id:" + alice, Assert.Single(partial.Reconciliation.Desired).PhysicalDirectoryKey);

        missing = false;
        var resolved = service.Discover([Location(players)], []);
        Assert.True(resolved.Reconciliation.IsComplete);
        Assert.Equal(pending, Assert.Single(resolved.Reconciliation.SuppressionIntents));
    }

    [Theory]
    [InlineData(SteamDiscoveryIssue.MetadataInvalid)]
    [InlineData(SteamDiscoveryIssue.LibraryUnavailable)]
    public void UncertainSteamDiscoveryRetainsSourcesAndPendingIntentsUntilResolvedOrRuleRemoved(SteamDiscoveryIssue issue)
    {
        const string installation = "C:\\SteamLibrary\\steamapps\\common\\Wurm";
        var players = Path.Combine(installation, "gamedata", "players");
        var alice = Path.Combine(players, "Alice");
        var bob = Path.Combine(players, "Bob");
        var profiles = new List<string> { alice, bob };
        var uncertain = false;
        using var fixture = new RegistryFixture();
        var locator = new WurmLogLocationLocator(_ => true, path => path == players ? profiles.ToArray() : [], path => path,
            new DelegateDirectoryPhysicalIdentityProvider(path => "id:" + path), () => "unused", _ => [],
            _ => uncertain
                ? new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), [new("D:\\SteamAlias", issue)])
                : new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>> { [1179680] = [installation] }, []),
            _ => [], _ => WurmLogDirectoryProbeResult.Exists);
        var service = new LogDiscoverySnapshotService(locator, fixture.CreateResolver(path => "id:" + path));
        var rule = new KnownLogLocation { Id = "custom-steam", Enabled = true, Game = "WurmOnline",
            Type = KnownLogLocations.SteamType, AppId = 1179680, PlayersRelativePath = Path.Combine("gamedata", "players") };

        var initial = service.Discover([rule], []);
        var aliceProfile = Assert.Single(initial.Profiles, profile => profile.ProfileDirectoryPath == alice);
        var aliceState = Assert.Single(initial.Reconciliation.Desired, state => state.PhysicalDirectoryKey == "id:" + alice);
        Assert.NotNull(aliceProfile.ProfileId);
        profiles.Remove(bob);
        var beforeFailure = service.Discover([rule], []);
        var pending = Assert.Single(beforeFailure.Reconciliation.SuppressionIntents);
        Assert.Equal("id:" + bob, pending.PhysicalDirectoryKey);

        uncertain = true;
        for (var i = 0; i < 2; i++)
        {
            var partial = service.Discover([rule], []);
            Assert.False(partial.IsComplete);
            Assert.False(partial.Reconciliation.IsComplete);
            Assert.False(Assert.Single(partial.Profiles).IsCurrent);
            Assert.Equal(aliceProfile.ProfileId, Assert.Single(partial.Profiles).ProfileId);
            var retained = Assert.Single(partial.Reconciliation.Desired);
            Assert.Equal(aliceState.PhysicalDirectoryKey, retained.PhysicalDirectoryKey);
            Assert.Equal(aliceState.Generation, retained.Generation);
            Assert.Equal(aliceState.PhysicalDirectoryKey, Assert.Single(partial.SourceCandidates).PhysicalDirectoryKey);
            Assert.Equal(pending, Assert.Single(partial.Reconciliation.SuppressionIntents));
        }

        uncertain = false;
        var recovered = service.Discover([rule], []);
        Assert.True(recovered.Reconciliation.IsComplete);
        Assert.Equal(aliceProfile.ProfileId, Assert.Single(recovered.Profiles).ProfileId);
        Assert.Equal(aliceState.Generation, Assert.Single(recovered.Reconciliation.Current).Generation);
        Assert.Equal(pending, Assert.Single(recovered.Reconciliation.SuppressionIntents));

        uncertain = true;
        Assert.False(service.Discover([rule], []).Reconciliation.IsComplete);
        var removed = service.Discover([], []);
        Assert.True(removed.Reconciliation.IsComplete);
        Assert.Contains(removed.Reconciliation.SuppressionIntents, intent => intent == pending);
        var aliceIntent = Assert.Single(removed.Reconciliation.SuppressionIntents, intent => intent.PhysicalDirectoryKey == aliceState.PhysicalDirectoryKey);
        Assert.Equal(aliceState.Generation, aliceIntent.Generation);
    }

    [Theory]
    [InlineData(SteamDiscoveryIssue.MetadataInvalid)]
    [InlineData(SteamDiscoveryIssue.LibraryUnavailable)]
    public void UncertainSteamScanBlocksPendingIntentEvenWithoutPriorCandidates(SteamDiscoveryIssue issue)
    {
        const string installation = "C:\\SteamLibrary\\steamapps\\common\\Wurm";
        var players = Path.Combine(installation, "gamedata", "players");
        var alice = Path.Combine(players, "Alice");
        var present = true;
        var uncertain = false;
        using var fixture = new RegistryFixture();
        var locator = new WurmLogLocationLocator(_ => true, path => path == players && present ? [alice] : [], path => path,
            new DelegateDirectoryPhysicalIdentityProvider(path => "id:" + path), () => "unused", _ => [],
            _ => uncertain
                ? new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), [new("steam", issue)])
                : new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>> { [1179680] = [installation] }, []),
            _ => [], _ => WurmLogDirectoryProbeResult.Exists);
        var service = new LogDiscoverySnapshotService(locator, fixture.CreateResolver(path => "id:" + path));
        var rule = new KnownLogLocation { Id = "steam", Enabled = true, Game = "WurmOnline",
            Type = KnownLogLocations.SteamType, AppId = 1179680, PlayersRelativePath = Path.Combine("gamedata", "players") };

        _ = service.Discover([rule], []);
        present = false;
        var absent = service.Discover([rule], []);
        Assert.Empty(absent.SourceCandidates);
        var pending = Assert.Single(absent.Reconciliation.SuppressionIntents);
        uncertain = true;
        var partial = service.Discover([rule], []);
        Assert.False(partial.Reconciliation.IsComplete);
        Assert.Equal(pending, Assert.Single(partial.Reconciliation.SuppressionIntents));
        uncertain = false;
        Assert.True(service.Discover([rule], []).Reconciliation.IsComplete);
    }

    [Fact]
    public void PhysicalDirectoryKeyIsStableAcrossSnapshotsAndAliasInputs()
    {
        const string target = "C:\\Target\\players";
        const string alias = "C:\\Alias\\players";
        const string profile = "C:\\Target\\players\\Alice";
        using var fixture = new RegistryFixture();
        var locator = new WurmLogLocationLocator(_ => true, path => path == target ? [profile] : [],
            path => path == alias ? target : path, new DelegateDirectoryPhysicalIdentityProvider(path => "id:" + path),
            () => "unused", _ => [], _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [], _ => WurmLogDirectoryProbeResult.Exists);
        var service = new LogDiscoverySnapshotService(locator, fixture.CreateResolver(_ => "profile-id"));

        var first = service.Discover([Location(target)], []);
        var second = service.Discover([Location(alias)], []);

        Assert.Equal("id:" + profile, Assert.Single(first.SourceCandidates).PhysicalDirectoryKey);
        Assert.Equal(Assert.Single(first.SourceCandidates).PhysicalDirectoryKey, Assert.Single(second.SourceCandidates).PhysicalDirectoryKey);
        Assert.Equal(1, Assert.Single(second.Reconciliation.Desired).Generation);
    }

    [Fact]
    public void CompleteReplacementAtSamePathCreatesIntentWithoutRelease()
    {
        const string players = "C:\\Data\\players";
        const string profile = "C:\\Data\\players\\Alice";
        var physicalKey = "physical:before";
        using var fixture = new RegistryFixture();
        var service = new LogDiscoverySnapshotService(CreateLocator(players, profile, Path.Combine(profile, "logs"),
            path => path == profile ? physicalKey : "id:" + path), fixture.CreateResolver(_ => "profile-id"));

        _ = service.Discover([Location(players)], []);
        physicalKey = "physical:after";
        var replacement = service.Discover([Location(players)], []);

        Assert.Equal("physical:after", Assert.Single(replacement.Reconciliation.Current).PhysicalDirectoryKey);
        var intent = Assert.Single(replacement.Reconciliation.SuppressionIntents);
        Assert.Equal("physical:before", intent.PhysicalDirectoryKey);
        Assert.Equal(1, intent.Generation);
        Assert.Empty(fixture.Resolver.GetActivationStatusSnapshot());
    }

    [Fact]
    public void MissingCurrentPhysicalIdentityDoesNotCreateRetirementIntent()
    {
        const string players = "C:\\Data\\players";
        const string profile = "C:\\Data\\players\\Alice";
        string? key = "physical:known";
        using var fixture = new RegistryFixture();
        var service = new LogDiscoverySnapshotService(CreateLocator(players, profile, Path.Combine(profile, "logs"),
            path => path == profile ? key : "id:" + path), fixture.CreateResolver(_ => "profile-id"));

        _ = service.Discover([Location(players)], []);
        key = null;
        var unknown = service.Discover([Location(players)], []);

        Assert.Null(Assert.Single(unknown.SourceCandidates).PhysicalDirectoryKey);
        Assert.Empty(unknown.Reconciliation.SuppressionIntents);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UncertainPhysicalIdentityRetainsDesiredUntilConfirmedRemoval(bool conflictingIdentity, bool resolveBeforeRemoval)
    {
        const string players = "C:\\Data\\players";
        const string alice = "C:\\Data\\players\\Alice";
        const string bob = "C:\\Data\\players\\Bob";
        var profiles = new List<string> { alice, bob };
        var uncertain = false;
        using var fixture = new RegistryFixture();
        var locator = new WurmLogLocationLocator(_ => true, path => path == players ? profiles.ToArray() : [], path => path,
            new DelegateDirectoryPhysicalIdentityProvider(path => path == alice && uncertain
                ? conflictingIdentity ? "physical:bob" : null
                : path == alice ? "physical:alice" : path == bob ? "physical:bob" : "id:" + path),
            () => "unused", _ => [], _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [], _ => WurmLogDirectoryProbeResult.Exists);
        var service = new LogDiscoverySnapshotService(locator, fixture.CreateResolver(path => "id:" + path));

        var first = service.Discover([Location(players)], []);
        Assert.Equal(2, first.Reconciliation.Desired.Count);
        uncertain = true;
        var ambiguous = service.Discover([Location(players)], []);
        Assert.True(ambiguous.IsComplete);
        Assert.Equal(2, ambiguous.Reconciliation.Desired.Count);
        Assert.All(ambiguous.Reconciliation.Desired, state => Assert.Equal(1, state.Generation));
        Assert.Contains(ambiguous.Reconciliation.Desired, state => state.PhysicalDirectoryKey == "physical:alice");
        Assert.Empty(ambiguous.Reconciliation.SuppressionIntents);

        uncertain = false;
        if (resolveBeforeRemoval)
        {
            var resolved = service.Discover([Location(players)], []);
            Assert.Equal(2, resolved.Reconciliation.Current.Count);
            Assert.All(resolved.Reconciliation.Desired, state => Assert.Equal(1, state.Generation));
            Assert.Empty(resolved.Reconciliation.SuppressionIntents);
        }

        profiles.Remove(alice);
        var removed = service.Discover([Location(players)], []);
        var intent = Assert.Single(removed.Reconciliation.SuppressionIntents);
        Assert.Equal("physical:alice", intent.PhysicalDirectoryKey);
        Assert.Equal(1, intent.Generation);
        Assert.Equal("physical:bob", Assert.Single(removed.Reconciliation.Desired).PhysicalDirectoryKey);
    }

    [Fact]
    public void UncertainReappearanceDoesNotDiscardEarlierSuppressionIntent()
    {
        const string players = "C:\\Data\\players";
        const string alice = "C:\\Data\\players\\Alice";
        const string bob = "C:\\Data\\players\\Bob";
        var profiles = new List<string> { alice, bob };
        var unknownBob = false;
        using var fixture = new RegistryFixture();
        var locator = new WurmLogLocationLocator(_ => true, path => path == players ? profiles.ToArray() : [], path => path,
            new DelegateDirectoryPhysicalIdentityProvider(path => path == bob && unknownBob ? null : "id:" + path),
            () => "unused", _ => [], _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [], _ => WurmLogDirectoryProbeResult.Exists);
        var service = new LogDiscoverySnapshotService(locator, fixture.CreateResolver(path => "id:" + path));
        _ = service.Discover([Location(players)], []);
        profiles.Remove(alice);
        var pending = Assert.Single(service.Discover([Location(players)], []).Reconciliation.SuppressionIntents);
        profiles.Add(alice);
        unknownBob = true;
        var uncertain = service.Discover([Location(players)], []);
        Assert.False(uncertain.Reconciliation.IsComplete);
        Assert.Contains(pending, uncertain.Reconciliation.SuppressionIntents);
    }

    [Fact]
    public void ReappearanceAfterCompleteAbsenceUsesNewGeneration()
    {
        const string players = "C:\\Data\\players";
        const string profile = "C:\\Data\\players\\Alice";
        var present = true;
        using var fixture = new RegistryFixture();
        var locator = new WurmLogLocationLocator(_ => true, path => path == players && present ? [profile] : [], path => path,
            new DelegateDirectoryPhysicalIdentityProvider(path => path == profile ? "physical:alice" : "id:" + path), () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []), _ => [], _ => WurmLogDirectoryProbeResult.Exists);
        var service = new LogDiscoverySnapshotService(locator, fixture.CreateResolver(_ => "profile-id"));

        var first = service.Discover([Location(players)], []);
        present = false;
        var absent = service.Discover([Location(players)], []);
        var stillAbsent = service.Discover([Location(players)], []);
        present = true;
        var returned = service.Discover([Location(players)], []);

        Assert.Equal(1, Assert.Single(first.Reconciliation.Desired).Generation);
        Assert.Equal(Assert.Single(absent.Reconciliation.SuppressionIntents), Assert.Single(stillAbsent.Reconciliation.SuppressionIntents));
        Assert.Equal(2, Assert.Single(returned.Reconciliation.Desired).Generation);
        Assert.Empty(returned.Reconciliation.SuppressionIntents);
    }
    [Fact]
    public void SnapshotKeepsSameNamedProfilesSeparateAndDoesNotAcquireSourceLeases()
    {
        var root = Path.Combine(Path.GetTempPath(), "WurmMiningCounter-Snapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Path.Combine(root, "one", "players", "Same", "logs");
            var second = Path.Combine(root, "two", "players", "Same", "logs");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "_Event.2026-10.txt"), string.Empty);
            File.WriteAllText(Path.Combine(first, "_Skills.2026-10.txt"), string.Empty);
            File.WriteAllText(Path.Combine(second, "_Event.2026-10.txt"), string.Empty);
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var service = new LogDiscoverySnapshotService(new WurmLogLocationLocator(), resolver);

            var snapshot = service.Discover(
                [new KnownLogLocation { Id = "one", Enabled = true, Game = "One", Type = KnownLogLocations.DirectType, Path = Path.Combine(root, "one", "players") },
                 new KnownLogLocation { Id = "two", Enabled = true, Game = "Two", Type = KnownLogLocations.DirectType, Path = Path.Combine(root, "two", "players") }], []);

            Assert.True(snapshot.IsComplete);
            Assert.Equal(2, snapshot.Profiles.Count);
            Assert.All(snapshot.Profiles, profile => Assert.True(profile.IsCurrent));
            Assert.All(snapshot.Profiles, profile => Assert.NotNull(profile.ProfileId));
            Assert.Equal(2, snapshot.Profiles.Select(profile => profile.ProfileId).Distinct().Count());
            Assert.Equal(2, snapshot.Reconciliation.Desired.Select(state => state.PhysicalDirectoryKey).Distinct().Count());
            Assert.Equal(2, snapshot.Profiles.Count(profile => profile.Files.Any(file => file.Kind == WurmLogFileKind.Event)));
            Assert.Equal(1, snapshot.Profiles.Count(profile => profile.Files.Any(file => file.Kind == WurmLogFileKind.Skills)));
            Assert.Empty(resolver.GetActivationStatusSnapshot());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static KnownLogLocation Location(string path, string id = "location") => new()
    {
        Id = id, Enabled = true, Game = "Test", Type = KnownLogLocations.DirectType, Path = path
    };

    private static WurmLogLocationLocator CreateLocator(string players, string profile, string logs, Func<string, string?> identity) =>
        new(_ => true, path => path == players ? [profile] : [], path => path,
            new DelegateDirectoryPhysicalIdentityProvider(identity), () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [Path.Combine(logs, "_Event.2026-10.txt"), Path.Combine(logs, "_Skills.2026-10.txt")],
            _ => WurmLogDirectoryProbeResult.Exists);

    private sealed class RegistryFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        internal PlayerProfileIdentityResolver Resolver { get; private set; } = null!;
        internal PlayerProfileIdentityResolver CreateResolver(Func<string, string?> identity)
        {
            Directory.CreateDirectory(_root);
            return Resolver = new PlayerProfileIdentityResolver(Path.Combine(_root, "registry"), new DelegateDirectoryPhysicalIdentityProvider(identity));
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
