using WurmMiningCounter.Services;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Tests.Services;

public sealed class WurmLogLocationLocatorTests
{
    [Fact]
    public void IncludesTheConfiguredDefaultPlayersRoot()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.CreateProfileLogs("Alice");
        var locator = new WurmLogLocationLocator(
            Directory.Exists,
            Directory.EnumerateDirectories,
            path => Path.GetFullPath(path),
            () => workspace.PlayersDirectoryPath);

        var result = locator.Locate(additionalLocations: null);

        var root = Assert.Single(result.DataRoots);
        Assert.Equal(Path.GetFullPath(workspace.PlayersDirectoryPath), root.PlayersDirectoryPath);
    }

    [Fact]
    public void RetainsEveryKnownRuleOriginForOneDeduplicatedPhysicalRoot()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.CreateProfileLogs("Alice");
        var online = new KnownLogLocation { Id = "online", Enabled = true, Game = "WurmOnline", Type = KnownLogLocations.DirectType, Path = workspace.PlayersDirectoryPath };
        var unlimited = new KnownLogLocation { Id = "unlimited", Enabled = true, Game = "WurmUnlimited", Type = KnownLogLocations.DirectType, Path = workspace.PlayersDirectoryPath };

        var root = Assert.Single(new WurmLogLocationLocator().Locate([online, unlimited]).DataRoots);

        Assert.Equal(["online", "unlimited"], root.Origins.Select(origin => origin.RuleId).Order());
        Assert.Equal(["WurmOnline", "WurmUnlimited"], root.Origins.Select(origin => origin.Game).Order());
        Assert.All(root.Origins, origin => Assert.False(origin.IsManual));
    }

    [Fact]
    public void AcceptsWurmPlayersAndLogsInputsForOneDeduplicatedRoot()
    {
        using var workspace = new TemporaryWorkspace();
        var logs = workspace.CreateProfileLogs("Alice");

        var result = new WurmLogLocationLocator().Locate([
            workspace.WurmDirectoryPath,
            workspace.PlayersDirectoryPath,
            logs
        ], includeDefaultLocation: false);

        var root = Assert.Single(result.DataRoots);
        Assert.Equal(Path.GetFullPath(workspace.PlayersDirectoryPath), root.PlayersDirectoryPath);
        var profile = Assert.Single(root.Profiles);
        Assert.Equal("Alice", profile.ProfileName);
        Assert.Equal(Path.GetFullPath(logs), profile.LogsDirectoryPath);
        Assert.Empty(result.ProfileNameConflicts);
    }

    [Fact]
    public void FindsMultipleProfilesEvenWhenLogsContainNoEntries()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.CreateProfileLogs("Alice");
        workspace.CreateProfileLogs("Bob");

        var result = new WurmLogLocationLocator().Locate([workspace.PlayersDirectoryPath], includeDefaultLocation: false);

        var root = Assert.Single(result.DataRoots);
        Assert.Equal(["Alice", "Bob"], root.Profiles.Select(profile => profile.ProfileName));
        Assert.Empty(result.Files);
        Assert.True(root.IsComplete);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void CatalogsRecognizedEventAndSkillsFilesWithoutInferringActivity()
    {
        using var workspace = new TemporaryWorkspace();
        var logs = workspace.CreateProfileLogs("Alice");
        foreach (var fileName in new[]
        {
            "_Event.2026-10.txt", "_Event.2026-10-11.txt",
            "_Skills.2026-10.txt", "_Skills.2026-10-11.txt",
            "_Event.2026-13.txt", "_Skills.2026-10-1.txt", "_Event.2026-10.log", "other.txt"
        })
            File.WriteAllText(Path.Combine(logs, fileName), string.Empty);

        var result = new WurmLogLocationLocator().Locate([workspace.PlayersDirectoryPath], includeDefaultLocation: false);

        Assert.Equal(4, result.Files.Count);
        Assert.Equal(2, result.Files.Count(file => file.Kind == WurmLogFileKind.Event));
        Assert.Equal(2, result.Files.Count(file => file.Kind == WurmLogFileKind.Skills));
        Assert.All(result.Files, file =>
        {
            Assert.Equal("Alice", file.ProfileName);
            Assert.Equal(logs, file.LogsDirectoryPath);
        });
        Assert.Equal(result.Files, result.DataRoots.Single().Profiles.Single().Files);
    }

    [Fact]
    public void InvalidFileCandidateDoesNotDiscardOtherCataloguedFiles()
    {
        const string players = "C:\\Data\\players";
        const string profile = "C:\\Data\\players\\Alice";
        const string logs = "C:\\Data\\players\\Alice\\logs";
        var validFile = Path.Combine(logs, "_Event.2026-10.txt");
        var locator = new WurmLogLocationLocator(
            path => path is players or logs,
            path => path == players ? [profile] : [],
            path => path,
            new DelegateDirectoryPhysicalIdentityProvider(_ => null),
            () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            path => path == logs ? FilesThenAccessFailure(validFile) : []);

        var result = locator.Locate([players], includeDefaultLocation: false);

        Assert.Equal(validFile, Assert.Single(result.Files).FilePath);
        Assert.Contains(result.Rejections, rejection => rejection.InputPath == logs && rejection.Reason == WurmLogLocationRejectionReason.AccessDenied);
    }

    [Fact]
    public void EnumeratesEachAcceptedLogsDirectoryOnlyOnceAndSkipsAmbiguousSources()
    {
        const string players = "C:\\Data\\players";
        const string alice = "C:\\Data\\players\\Alice";
        const string alias = "C:\\Data\\players\\Alias";
        const string aliceLogs = "C:\\Data\\players\\Alice\\logs";
        const string aliasLogs = "C:\\Data\\players\\Alias\\logs";
        var enumerations = 0;
        var locator = new WurmLogLocationLocator(
            path => path is players or aliceLogs or aliasLogs,
            path => path == players ? [alice, alias] : [],
            path => path,
            new DelegateDirectoryPhysicalIdentityProvider(path => path == players ? "players" : "shared-logs"),
            () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => { enumerations++; return [Path.Combine(aliceLogs, "_Event.2026-10.txt")]; });

        var result = locator.Locate([players], includeDefaultLocation: false);

        Assert.Empty(result.Files);
        Assert.Single(result.SourceOwnershipAmbiguities);
        Assert.Equal(0, enumerations);
    }

    [Fact]
    public void DuplicateRootsDoNotCreateProfileNameConflicts()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.CreateProfileLogs("Alice");

        var result = new WurmLogLocationLocator().Locate([
            workspace.WurmDirectoryPath,
            workspace.PlayersDirectoryPath
        ], includeDefaultLocation: false);

        Assert.Single(result.DataRoots);
        Assert.Empty(result.ProfileNameConflicts);
    }

    [Fact]
    public void CanonicalizedDirectoryAliasesAreDeduplicated()
    {
        const string aliasPlayersPath = "C:\\Alias\\players";
        const string targetPlayersPath = "C:\\Target\\players";
        const string targetProfilePath = "C:\\Target\\players\\Alice";
        const string targetLogsPath = "C:\\Target\\players\\Alice\\logs";
        var locator = new WurmLogLocationLocator(
            path => string.Equals(path, targetPlayersPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, targetLogsPath, StringComparison.OrdinalIgnoreCase),
            path => string.Equals(path, targetPlayersPath, StringComparison.OrdinalIgnoreCase)
                ? [targetProfilePath]
                : [],
            path => string.Equals(path, aliasPlayersPath, StringComparison.OrdinalIgnoreCase)
                ? targetPlayersPath
                : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var result = locator.Locate([aliasPlayersPath, targetPlayersPath], includeDefaultLocation: false);

        var root = Assert.Single(result.DataRoots);
        Assert.Equal(targetPlayersPath, root.PlayersDirectoryPath);
    }

    [Fact]
    public void ReportsCaseInsensitiveProfileNameConflictAcrossRoots()
    {
        using var first = new TemporaryWorkspace();
        using var second = new TemporaryWorkspace();
        first.CreateProfileLogs("Alice");
        second.CreateProfileLogs("alice");

        var result = new WurmLogLocationLocator().Locate([
            first.PlayersDirectoryPath,
            second.PlayersDirectoryPath
        ], includeDefaultLocation: false);

        var conflict = Assert.Single(result.ProfileNameConflicts);
        Assert.Equal("Alice", conflict.ProfileName, ignoreCase: true);
        Assert.Equal(2, conflict.Sources.Count);
        Assert.Equal(2, result.DataRoots.Count);
    }

    [Fact]
    public void MissingDirectoryIsRejectedWithoutThrowing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var result = new WurmLogLocationLocator().Locate([missingPath], includeDefaultLocation: false);

        Assert.Contains(result.Rejections, rejection =>
            rejection.InputPath == missingPath
            && rejection.Reason == WurmLogLocationRejectionReason.Missing);
    }

    [Fact]
    public void AccessDeniedDuringProfileEnumerationIsRejectedWithoutThrowing()
    {
        const string playersPath = "C:\\Test\\players";
        var locator = new WurmLogLocationLocator(
            path => string.Equals(path, playersPath, StringComparison.OrdinalIgnoreCase),
            _ => throw new UnauthorizedAccessException(),
            path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var result = locator.Locate([playersPath], includeDefaultLocation: false);

        var rejection = Assert.Single(result.Rejections.Where(item => item.InputPath == playersPath));
        Assert.Equal(WurmLogLocationRejectionReason.AccessDenied, rejection.Reason);
        Assert.Empty(Assert.Single(result.DataRoots).Profiles);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void RejectedRootAccessDeniedMakesAnOtherwiseEmptyScanIncomplete()
    {
        const string playersPath = "C:\\Denied\\players";
        var locator = new WurmLogLocationLocator(
            _ => true, _ => [], path => path,
            new DelegateDirectoryPhysicalIdentityProvider(_ => null),
            () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [], _ => WurmLogDirectoryProbeResult.AccessDenied);

        var result = locator.Locate([playersPath], includeDefaultLocation: false);

        Assert.Empty(result.DataRoots);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Rejections, rejection => rejection.InputPath == playersPath
            && rejection.Reason == WurmLogLocationRejectionReason.AccessDenied);
    }

    [Fact]
    public void HealthyRootIsRetainedWhenAnotherRootIsRejectedByIoFailure()
    {
        const string healthy = "C:\\Data\\players";
        const string profile = "C:\\Data\\players\\Alice";
        const string denied = "C:\\Denied\\players";
        var locator = new WurmLogLocationLocator(
            _ => true,
            path => path == healthy ? [profile] : [], path => path,
            new DelegateDirectoryPhysicalIdentityProvider(_ => null),
            () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [], path => path == denied ? WurmLogDirectoryProbeResult.IoFailure : WurmLogDirectoryProbeResult.Exists);

        var result = locator.Locate([healthy, denied], includeDefaultLocation: false);

        Assert.Equal("Alice", Assert.Single(Assert.Single(result.DataRoots).Profiles).ProfileName);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Rejections, rejection => rejection.InputPath == denied
            && rejection.Reason == WurmLogLocationRejectionReason.IoFailure);
    }

    [Fact]
    public void CompleteScanWithoutRootsRemainsComplete()
    {
        var result = new WurmLogLocationLocator().Locate([], includeDefaultLocation: false);

        Assert.Empty(result.DataRoots);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void PartialProfileEnumerationRetainsProfilesAlreadyReturnedAndMarksTheResultIncomplete()
    {
        const string players = "C:\\Data\\players";
        const string alice = "C:\\Data\\players\\Alice";
        const string aliceLogs = "C:\\Data\\players\\Alice\\logs";
        var locator = new WurmLogLocationLocator(
            path => path is players or aliceLogs,
            path => path == players ? DirectoriesThenIOException(alice) : [],
            path => path,
            () => "unused");

        var result = locator.Locate([players], includeDefaultLocation: false);

        Assert.Equal("Alice", Assert.Single(result.DataRoots.Single().Profiles).ProfileName);
        Assert.False(result.DataRoots.Single().IsComplete);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Rejections, rejection => rejection.InputPath == players
            && rejection.Reason == WurmLogLocationRejectionReason.IoFailure);
    }

    [Fact]
    public void LogsAccessFailureDoesNotRemoveOtherProfilesOrMasqueradeAsAnEmptyLogsDirectory()
    {
        const string players = "C:\\Data\\players";
        const string alice = "C:\\Data\\players\\Alice";
        const string bob = "C:\\Data\\players\\Bob";
        const string aliceLogs = "C:\\Data\\players\\Alice\\logs";
        const string bobLogs = "C:\\Data\\players\\Bob\\logs";
        var locator = new WurmLogLocationLocator(
            _ => false,
            path => path == players ? [alice, bob] : [],
            path => path,
            new DelegateDirectoryPhysicalIdentityProvider(_ => null),
            () => "unused",
            _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            null,
            path => path switch
            {
                var candidate when candidate == players || candidate == bobLogs => WurmLogDirectoryProbeResult.Exists,
                var candidate when candidate == aliceLogs => WurmLogDirectoryProbeResult.AccessDenied,
                _ => WurmLogDirectoryProbeResult.Missing
            });

        var result = locator.Locate([players], includeDefaultLocation: false);

        Assert.Equal("Bob", Assert.Single(result.DataRoots.Single().Profiles).ProfileName);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Rejections, rejection => rejection.InputPath == alice
            && rejection.Reason == WurmLogLocationRejectionReason.AccessDenied);
    }

    [Fact]
    public void MissingAndAccessDeniedRootsHaveDifferentRejections()
    {
        const string missing = "C:\\Data\\missing";
        const string denied = "C:\\Data\\denied";
        var locator = new WurmLogLocationLocator(
            _ => false,
            _ => [],
            path => path,
            new DelegateDirectoryPhysicalIdentityProvider(_ => null),
            () => "unused",
            _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            null,
            path => path == denied ? WurmLogDirectoryProbeResult.AccessDenied : WurmLogDirectoryProbeResult.Missing);

        var result = locator.Locate([missing, denied], includeDefaultLocation: false);

        Assert.Contains(result.Rejections, rejection => rejection.InputPath == missing
            && rejection.Reason == WurmLogLocationRejectionReason.Missing);
        Assert.Contains(result.Rejections, rejection => rejection.InputPath == denied
            && rejection.Reason == WurmLogLocationRejectionReason.AccessDenied);
    }

    [Fact]
    public void MissingPlayersAndMissingProfileLogsAreDistinctWhileEmptyLogsRemainValid()
    {
        const string players = "C:\\Data\\players";
        const string missingPlayers = "C:\\Data\\missing-players";
        const string incompleteProfile = "C:\\Data\\players\\Incomplete";
        const string emptyProfile = "C:\\Data\\players\\Empty";
        const string emptyLogs = "C:\\Data\\players\\Empty\\logs";
        var locator = new WurmLogLocationLocator(
            _ => false,
            path => path == players ? [incompleteProfile, emptyProfile] : [],
            path => path,
            new DelegateDirectoryPhysicalIdentityProvider(_ => null),
            () => "unused",
            _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [],
            path => path == players || path == emptyLogs
                ? WurmLogDirectoryProbeResult.Exists
                : WurmLogDirectoryProbeResult.Missing);

        var result = locator.Locate([players, missingPlayers], includeDefaultLocation: false);

        Assert.Contains(result.Rejections, rejection => rejection.InputPath == missingPlayers
            && rejection.Reason == WurmLogLocationRejectionReason.Missing);
        Assert.Contains(result.Rejections, rejection => rejection.InputPath == incompleteProfile
            && rejection.Reason == WurmLogLocationRejectionReason.ProfileLogsFolderMissing);
        var profile = Assert.Single(Assert.Single(result.DataRoots).Profiles);
        Assert.Equal("Empty", profile.ProfileName);
        Assert.Empty(profile.Files);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void InvalidProfileDoesNotRejectOtherProfilesInTheSameRoot()
    {
        const string players = "C:\\Data\\players";
        const string goodProfile = "C:\\Data\\players\\Alice";
        const string brokenProfile = "C:\\Data\\players\\Broken";
        const string goodLogs = "C:\\Data\\players\\Alice\\logs";
        const string brokenLogs = "C:\\Data\\players\\Broken\\logs";
        var locator = new WurmLogLocationLocator(
            path => path is players or goodLogs or brokenLogs,
            path => path == players ? [goodProfile, brokenProfile] : [],
            path => path == brokenLogs ? throw new UnauthorizedAccessException() : path,
            path => path,
            () => "unused", _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []));

        var result = locator.Locate([players], includeDefaultLocation: false);

        Assert.Equal("Alice", Assert.Single(result.DataRoots.Single().Profiles).ProfileName);
        Assert.Contains(result.Rejections, rejection => rejection.InputPath == brokenProfile && rejection.Reason == WurmLogLocationRejectionReason.AccessDenied);
    }

    [Fact]
    public void RejectsAnInstallationDirectoryWithoutPlayersChild()
    {
        using var workspace = new TemporaryWorkspace();
        var installationPath = Path.Combine(workspace.RootDirectoryPath, "Wurm Online");
        Directory.CreateDirectory(installationPath);

        var result = new WurmLogLocationLocator().Locate([installationPath], includeDefaultLocation: false);

        Assert.Contains(result.Rejections, rejection =>
            rejection.InputPath == installationPath
            && rejection.Reason == WurmLogLocationRejectionReason.InvalidStructure);
    }

    [Fact]
    public void ResolvesSteamRulesAcrossMultipleLibrariesAndSkipsMissingInstallations()
    {
        using var first = new TemporaryWorkspace();
        using var second = new TemporaryWorkspace();
        first.CreateProfileLogs("Alice");
        second.CreateProfileLogs("Bob");
        var firstInstallation = Path.Combine(first.RootDirectoryPath, "Wurm Online");
        var secondInstallation = Path.Combine(second.RootDirectoryPath, "Wurm Online");
        Directory.CreateDirectory(firstInstallation);
        Directory.CreateDirectory(secondInstallation);
        Directory.Move(first.WurmDirectoryPath, Path.Combine(firstInstallation, "gamedata"));
        Directory.Move(second.WurmDirectoryPath, Path.Combine(secondInstallation, "gamedata"));
        var locator = new WurmLogLocationLocator(
            Directory.Exists, Directory.EnumerateDirectories, Path.GetFullPath, () => "unused",
            appId => appId == 1179680 ? [firstInstallation, secondInstallation] : []);

        var result = locator.Locate([
            new KnownLogLocation { Id = "steam", Enabled = true, Game = "WurmOnline", Type = "Steam", AppId = 1179680, PlayersRelativePath = Path.Combine("gamedata", "players") },
            new KnownLogLocation { Id = "missing", Enabled = true, Game = "WurmUnlimited", Type = "Steam", AppId = 366220, PlayersRelativePath = "players" }
        ]);

        Assert.Equal(2, result.DataRoots.Count);
        Assert.Equal(["Alice", "Bob"], result.DataRoots.SelectMany(root => root.Profiles).Select(profile => profile.ProfileName).Order());
    }

    [Fact]
    public void RejectsInvalidRulesAndDoesNotActivateMissingDirectLocations()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var result = new WurmLogLocationLocator().Locate([
            new KnownLogLocation { Id = "invalid", Enabled = true, Game = "WurmOnline", Type = "Steam", AppId = 1179680, PlayersRelativePath = @"C:\\rooted" },
            new KnownLogLocation { Id = "missing", Enabled = true, Game = "WurmOnline", Type = "Direct", Path = missingPath }
        ]);

        Assert.Contains(result.Rejections, item => item.InputPath == "invalid" && item.Reason == WurmLogLocationRejectionReason.InvalidStructure);
        Assert.Contains(result.Rejections, item => item.InputPath == missingPath && item.Reason == WurmLogLocationRejectionReason.Missing);
        Assert.Empty(result.DataRoots);
    }

    [Fact]
    public void NullAndTraversalRulesAreRejectedWithoutBlockingValidRules()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.CreateProfileLogs("Alice");
        var result = new WurmLogLocationLocator().Locate([
            null!,
            new KnownLogLocation
            {
                Id = "traversal", Enabled = true, Game = "WurmOnline", Type = "Steam",
                AppId = 1179680, PlayersRelativePath = Path.Combine("..", "players")
            },
            new KnownLogLocation
            {
                Id = "valid", Enabled = true, Game = "WurmOnline", Type = "Direct",
                Path = workspace.PlayersDirectoryPath
            }
        ]);

        Assert.Single(result.DataRoots);
        Assert.Equal("Alice", Assert.Single(result.DataRoots.Single().Profiles).ProfileName);
        Assert.Equal(2, result.Rejections.Count(item => item.Reason == WurmLogLocationRejectionReason.InvalidStructure));
    }

    [Fact]
    public void ResolvesAllSteamRulesInOneBatchDiscovery()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.CreateProfileLogs("Alice");
        var installation = Path.Combine(workspace.RootDirectoryPath, "Wurm Online");
        Directory.CreateDirectory(installation);
        Directory.Move(workspace.WurmDirectoryPath, Path.Combine(installation, "gamedata"));
        var discoveries = 0;
        var locator = new WurmLogLocationLocator(
            Directory.Exists, Directory.EnumerateDirectories, Path.GetFullPath, () => "unused",
            _ => throw new InvalidOperationException("Single-app discovery should not be used."),
            appIds =>
            {
                discoveries++;
                Assert.Equal([1179680], appIds.Order());
                return new SteamInstallationDiscoveryResult(
                    new Dictionary<int, IReadOnlyList<string>> { [1179680] = [installation] }, []);
            });

        var result = locator.Locate([
            new KnownLogLocation { Id = "one", Enabled = true, Game = "WurmOnline", Type = "Steam", AppId = 1179680, PlayersRelativePath = Path.Combine("gamedata", "players") },
            new KnownLogLocation { Id = "two", Enabled = true, Game = "WurmOnline", Type = "Steam", AppId = 1179680, PlayersRelativePath = Path.Combine("gamedata", "players") }
        ]);

        Assert.Equal(1, discoveries);
        Assert.Single(result.DataRoots);
    }

    [Fact]
    public void KeepsLogicalPlayersValidationWhenLinkTargetHasADifferentName()
    {
        const string playersAlias = "C:\\Alias\\players";
        const string physicalRoot = "C:\\Physical\\data";
        const string profile = "C:\\Physical\\data\\Alice";
        const string logs = "C:\\Physical\\data\\Alice\\logs";
        var locator = new WurmLogLocationLocator(
            path => path is playersAlias or physicalRoot or logs,
            path => path == physicalRoot ? [profile] : [],
            path => path == playersAlias ? physicalRoot : path,
            path => path == physicalRoot ? "volume:file-1" : path == logs ? "volume:file-2" : null,
            () => "unused",
            _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []));

        var result = locator.Locate([playersAlias], includeDefaultLocation: false);

        var root = Assert.Single(result.DataRoots);
        Assert.Equal(physicalRoot, root.PlayersDirectoryPath);
        Assert.Equal("Alice", Assert.Single(root.Profiles).ProfileName);
    }

    [Fact]
    public void DeduplicatesDifferentAliasesOnlyWhenPhysicalIdentityMatches()
    {
        const string firstAlias = "C:\\First\\players";
        const string secondAlias = "C:\\Second\\players";
        const string firstProfile = "C:\\First\\players\\Alice";
        const string secondProfile = "C:\\Second\\players\\Alice";
        const string firstLogs = "C:\\First\\players\\Alice\\logs";
        const string secondLogs = "C:\\Second\\players\\Alice\\logs";
        var locator = new WurmLogLocationLocator(
            path => path is firstAlias or secondAlias or firstLogs or secondLogs,
            path => path == firstAlias ? [firstProfile] : path == secondAlias ? [secondProfile] : [],
            path => path,
            path => path is firstAlias or secondAlias ? "volume:players" : path is firstLogs or secondLogs ? "volume:logs" : null,
            () => "unused",
            _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []));

        var result = locator.Locate([firstAlias, secondAlias], includeDefaultLocation: false);

        Assert.Single(result.DataRoots);
        Assert.Empty(result.Uncertainties);
    }

    [Fact]
    public void DoesNotMergeDifferentAliasesWhenPhysicalIdentityIsUnavailable()
    {
        const string firstAlias = "C:\\First\\players";
        const string secondAlias = "C:\\Second\\players";
        const string firstProfile = "C:\\First\\players\\Alice";
        const string secondProfile = "C:\\Second\\players\\Alice";
        const string firstLogs = "C:\\First\\players\\Alice\\logs";
        const string secondLogs = "C:\\Second\\players\\Alice\\logs";
        var locator = new WurmLogLocationLocator(
            path => path is firstAlias or secondAlias or firstLogs or secondLogs,
            path => path == firstAlias ? [firstProfile] : path == secondAlias ? [secondProfile] : [],
            path => path,
            _ => (string?)null,
            () => "unused",
            _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []));

        var result = locator.Locate([firstAlias, secondAlias], includeDefaultLocation: false);

        Assert.Equal(2, result.DataRoots.Count);
        Assert.Equal(4, result.Uncertainties.Count);
        Assert.Single(result.ProfileNameConflicts);
    }

    [Fact]
    public void ReportsAmbiguousOwnershipOfSharedLogsWithinOneRoot()
    {
        const string players = "C:\\Data\\players";
        const string firstProfile = "C:\\Data\\players\\Alice";
        const string secondProfile = "C:\\Data\\players\\AliasAlice";
        const string firstLogs = "C:\\Data\\players\\Alice\\logs";
        const string secondLogs = "C:\\Data\\players\\AliasAlice\\logs";
        var locator = new WurmLogLocationLocator(
            path => path is players or firstLogs or secondLogs,
            path => path == players ? [firstProfile, secondProfile] : [],
            path => path,
            path => path == players ? "volume:players" : path is firstLogs or secondLogs ? "volume:shared-logs" : null,
            () => "unused",
            _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []));

        var result = locator.Locate([players], includeDefaultLocation: false);

        Assert.Single(result.DataRoots);
        Assert.Empty(result.DataRoots.Single().Profiles);
        var ambiguity = Assert.Single(result.SourceOwnershipAmbiguities);
        Assert.Equal(2, ambiguity.Claims.Count);
        Assert.Equal(["AliasAlice", "Alice"], ambiguity.Claims.Select(claim => claim.Profile.ProfileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProbesEachCanonicalDirectoryIdentityAtMostOncePerDiscovery()
    {
        const string players = "C:\\Data\\players";
        const string profile = "C:\\Data\\players\\Alice";
        const string logs = "C:\\Data\\players\\Alice\\logs";
        var probes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var locator = new WurmLogLocationLocator(
            path => path is players or logs,
            path => path == players ? [profile] : [],
            path => path,
            path =>
            {
                probes[path] = probes.GetValueOrDefault(path) + 1;
                return path;
            },
            () => "unused",
            _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []));

        var result = locator.Locate([players, players], includeDefaultLocation: false);

        Assert.Single(result.DataRoots);
        Assert.Equal(1, probes[players]);
        Assert.Equal(1, probes[profile]);
        Assert.Equal(1, probes[logs]);
        Assert.Equal(3, probes.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedPhysicalLogsAcrossRootsProduceOneSourceWithoutNameConflict(bool reverseInputs)
    {
        var fixture = new GlobalLogsFixture(sharedIdentity: true, unknownLogsIdentity: false, secondName: "Alice");
        var rule = new KnownLogLocation { Id = "direct", Game = "WurmOnline", Type = "Direct", Path = fixture.FirstRoot };
        var settings = new AppSettings { KnownLogLocations = [rule], AdditionalWurmDataLocations = [fixture.SecondRoot] };

        var result = reverseInputs
            ? fixture.Locator.Locate([fixture.SecondRoot, fixture.FirstRoot], includeDefaultLocation: false)
            : fixture.Locator.Locate(settings.KnownLogLocations, settings.AdditionalWurmDataLocations);

        Assert.Equal(2, result.DataRoots.Count);
        var profile = Assert.Single(result.DataRoots.SelectMany(root => root.Profiles));
        Assert.Equal("Alice", profile.ProfileName);
        Assert.Equal(fixture.FirstLogs, profile.LogicalLogsDirectoryPath);
        Assert.Empty(result.ProfileNameConflicts);
        Assert.Empty(result.SourceOwnershipAmbiguities);
        Assert.Empty(result.Uncertainties);
        Assert.All(fixture.IdentityReads.Values, count => Assert.Equal(1, count));
        Assert.Equal(6, fixture.IdentityReads.Count);
        Assert.Equal(fixture.FirstRoot, rule.Path);
        Assert.Equal([fixture.SecondRoot], settings.AdditionalWurmDataLocations);
        Assert.Equal([fixture.FirstRoot, fixture.SecondRoot], result.DataRoots.SelectMany(root => root.InputPaths).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeparateOrUnconfirmedLogsAcrossRootsRemainDistinct(bool unknownIdentity)
    {
        var fixture = new GlobalLogsFixture(sharedIdentity: false, unknownLogsIdentity: unknownIdentity, secondName: "Alice");
        var result = fixture.Locator.Locate([fixture.FirstRoot, fixture.SecondRoot], includeDefaultLocation: false);

        Assert.Equal(2, result.DataRoots.SelectMany(root => root.Profiles).Count());
        Assert.Equal(2, Assert.Single(result.ProfileNameConflicts).Sources.Count);
        Assert.Empty(result.SourceOwnershipAmbiguities);
        Assert.Equal(unknownIdentity ? 2 : 0, result.Uncertainties.Count);
        if (unknownIdentity)
            Assert.Equal([fixture.FirstLogs, fixture.SecondLogs], result.Uncertainties.Select(item => item.DirectoryPath).Order());
    }

    [Fact]
    public void SharedLogsWithDifferentProfileNamesAcrossRootsHaveNoArbitraryOwner()
    {
        var fixture = new GlobalLogsFixture(sharedIdentity: true, unknownLogsIdentity: false, secondName: "Bob");
        var result = fixture.Locator.Locate([fixture.FirstRoot, fixture.SecondRoot], includeDefaultLocation: false);

        Assert.Empty(result.DataRoots.SelectMany(root => root.Profiles));
        Assert.Empty(result.ProfileNameConflicts);
        var ambiguity = Assert.Single(result.SourceOwnershipAmbiguities);
        Assert.Equal(["Alice", "Bob"], ambiguity.Claims.Select(claim => claim.Profile.ProfileName));
        Assert.Equal([fixture.FirstLogs, fixture.SecondLogs], ambiguity.Claims.Select(claim => claim.Profile.LogicalLogsDirectoryPath));
        Assert.Equal([fixture.FirstRoot, fixture.SecondRoot], ambiguity.Claims.Select(claim => claim.PlayersDirectoryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedCanonicalLogsUseOneIdentityProbeAndRetainLogicalPaths(bool unknownIdentity)
    {
        var fixture = new GlobalLogsFixture(sharedIdentity: true, unknownLogsIdentity: unknownIdentity,
            secondName: "Alice", canonicalizeSharedLogs: true);
        var result = fixture.Locator.Locate([fixture.FirstRoot, fixture.SecondRoot], includeDefaultLocation: false);

        var profile = Assert.Single(result.DataRoots.SelectMany(root => root.Profiles));
        Assert.Equal(fixture.CanonicalLogs, profile.LogsDirectoryPath);
        Assert.Equal(fixture.FirstLogs, profile.LogicalLogsDirectoryPath);
        Assert.Equal(1, fixture.IdentityReads[fixture.CanonicalLogs]);
        Assert.Equal(5, fixture.IdentityReads.Count);
        Assert.Empty(result.ProfileNameConflicts);
        Assert.Equal(unknownIdentity ? 1 : 0, result.Uncertainties.Count);
    }

    private sealed class GlobalLogsFixture
    {
        public string FirstRoot { get; } = @"C:\First\players";
        public string SecondRoot { get; } = @"C:\Second\players";
        public string FirstLogs { get; }
        public string SecondLogs { get; }
        public string CanonicalLogs { get; } = @"C:\Physical\logs";
        public Dictionary<string, int> IdentityReads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public WurmLogLocationLocator Locator { get; }

        public GlobalLogsFixture(bool sharedIdentity, bool unknownLogsIdentity, string secondName, bool canonicalizeSharedLogs = false)
        {
            var firstProfile = Path.Combine(FirstRoot, "Alice");
            var secondProfile = Path.Combine(SecondRoot, secondName);
            FirstLogs = Path.Combine(firstProfile, "logs");
            SecondLogs = Path.Combine(secondProfile, "logs");
            Locator = new WurmLogLocationLocator(
                path => path == FirstRoot || path == SecondRoot || path == FirstLogs || path == SecondLogs,
                path => path == FirstRoot ? [firstProfile] : path == SecondRoot ? [secondProfile] : [],
                path => canonicalizeSharedLogs && (path == FirstLogs || path == SecondLogs) ? CanonicalLogs : path,
                path =>
                {
                    IdentityReads[path] = IdentityReads.GetValueOrDefault(path) + 1;
                    if (path == FirstRoot || path == SecondRoot)
                        return path;
                    return unknownLogsIdentity ? null : sharedIdentity ? "shared-logs-id" : path;
                },
                () => "unused", _ => [],
                _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []));
        }
    }

    private static IEnumerable<string> FilesThenAccessFailure(string validFile)
    {
        yield return validFile;
        throw new UnauthorizedAccessException();
    }

    private static IEnumerable<string> DirectoriesThenIOException(string firstDirectory)
    {
        yield return firstDirectory;
        throw new IOException();
    }

    [RequiresDirectorySymbolicLinkFact]
    public void ResolvesRealDirectoryLinkNamedPlayersWhenSupported()
    {
        using var workspace = new TemporaryWorkspace();
        var physicalRoot = Path.Combine(workspace.RootDirectoryPath, "physical-data", "players");
        Directory.CreateDirectory(Path.Combine(physicalRoot, "Alice", "logs"));
        var aliasParent = Path.Combine(workspace.RootDirectoryPath, "alias");
        Directory.CreateDirectory(aliasParent);
        var playersLink = Path.Combine(aliasParent, "players");
        Directory.CreateSymbolicLink(playersLink, physicalRoot);

        var result = new WurmLogLocationLocator().Locate([physicalRoot, playersLink], includeDefaultLocation: false);

        var root = Assert.Single(result.DataRoots);
        Assert.Single(root.Profiles);
        Assert.Equal("Alice", root.Profiles.Single().ProfileName);
        Assert.Equal(2, root.Origins.Count);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            RootDirectoryPath = Path.Combine(Path.GetTempPath(), $"WurmMiningCounter.Tests-{Guid.NewGuid():N}");
            WurmDirectoryPath = Path.Combine(RootDirectoryPath, "wurm");
            PlayersDirectoryPath = Path.Combine(WurmDirectoryPath, "players");
            Directory.CreateDirectory(PlayersDirectoryPath);
        }

        public string RootDirectoryPath { get; }
        public string WurmDirectoryPath { get; }
        public string PlayersDirectoryPath { get; }

        public string CreateProfileLogs(string profileName)
        {
            var logsPath = Path.Combine(PlayersDirectoryPath, profileName, "logs");
            Directory.CreateDirectory(logsPath);
            return logsPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectoryPath))
                Directory.Delete(RootDirectoryPath, recursive: true);
        }
    }
}

public sealed class RequiresDirectorySymbolicLinkFactAttribute : FactAttribute
{
    public RequiresDirectorySymbolicLinkFactAttribute()
    {
        Skip = GetSkipReason();
    }

    private static string? GetSkipReason()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WurmMiningCounter.LinkProbe-{Guid.NewGuid():N}");
        try
        {
            var target = Path.Combine(root, "target");
            var link = Path.Combine(root, "link");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            return $"Directory symbolic links require unavailable system permission: {exception.Message}";
        }
        catch (IOException exception)
        {
            return $"Directory symbolic links are unavailable in this environment: {exception.Message}";
        }
        catch (PlatformNotSupportedException exception)
        {
            return $"Directory symbolic links are unsupported: {exception.Message}";
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
