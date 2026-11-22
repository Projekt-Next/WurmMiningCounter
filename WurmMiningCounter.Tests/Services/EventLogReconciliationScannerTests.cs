using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventLogReconciliationScannerTests
{
    [Fact]
    public void MissingPlayersDirectoryReturnsEmpty()
    {
        var scanner = new EventLogReconciliationScanner();
        var missingPlayersDirectoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var candidates = scanner.Scan(missingPlayersDirectoryPath);

        Assert.Empty(candidates);
    }

    [Fact]
    public void EnumerationFailureReturnsEmptyWithoutThrowing()
    {
        using var workspace = new TemporaryWorkspace();
        var logsDirectoryPath = workspace.CreateProfileLogsDirectory("Alice");
        var scanner = new EventLogReconciliationScanner(
            _ => [Path.GetDirectoryName(logsDirectoryPath)!],
            _ => throw new IOException("Simulated file enumeration failure."));

        var exception = Record.Exception(() =>
            scanner.Scan(workspace.PlayersDirectoryPath));

        Assert.Null(exception);
        Assert.Empty(scanner.Scan(workspace.PlayersDirectoryPath));
    }

    [Fact]
    public void ReturnsOnlyEventTxtFromDirectLogs()
    {
        using var workspace = new TemporaryWorkspace();
        var logsDirectoryPath = workspace.CreateProfileLogsDirectory("Alice");
        var eventFilePath = workspace.CreateFile(logsDirectoryPath, "_Event.2026-08.txt");
        workspace.CreateFile(logsDirectoryPath, "Other.txt");
        workspace.CreateFile(logsDirectoryPath, "Event.2026-08.txt");
        workspace.CreateFile(logsDirectoryPath, "backup_Event.2026-08.txt");

        var candidates = new EventLogReconciliationScanner().Scan(workspace.PlayersDirectoryPath);

        var candidate = Assert.Single(candidates);
        Assert.Equal(Path.GetFullPath(eventFilePath), candidate.EventFilePath);
    }

    [Theory]
    [InlineData("_Event.2026-08.txt")]
    [InlineData("_Event.2026-08.TXT")]
    [InlineData("_Event.2026-08.Txt")]
    public void ReturnsEventLogsWithCaseInsensitiveTxtExtension(string fileName)
    {
        using var workspace = new TemporaryWorkspace();
        var logsDirectoryPath = workspace.CreateProfileLogsDirectory("Alice");
        var eventFilePath = workspace.CreateFile(logsDirectoryPath, fileName);

        var candidate = Assert.Single(new EventLogReconciliationScanner().Scan(workspace.PlayersDirectoryPath));

        Assert.Equal(Path.GetFullPath(eventFilePath), candidate.EventFilePath);
    }

    [Fact]
    public void DoesNotScanNestedDirectoriesInsideLogs()
    {
        using var workspace = new TemporaryWorkspace();
        var logsDirectoryPath = workspace.CreateProfileLogsDirectory("Alice");
        var directEventFilePath = workspace.CreateFile(logsDirectoryPath, "_Event.2026-08.txt");
        var nestedLogsDirectoryPath = Path.Combine(logsDirectoryPath, "archive");
        Directory.CreateDirectory(nestedLogsDirectoryPath);
        workspace.CreateFile(nestedLogsDirectoryPath, "_Event.2026-08.txt");

        var candidates = new EventLogReconciliationScanner().Scan(workspace.PlayersDirectoryPath);

        var candidate = Assert.Single(candidates);
        Assert.Equal(Path.GetFullPath(directEventFilePath), candidate.EventFilePath);
    }

    [Fact]
    public void ProfileWithoutLogsIsIgnored()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.CreateProfileDirectory("WithoutLogs");
        var logsDirectoryPath = workspace.CreateProfileLogsDirectory("Alice");
        workspace.CreateFile(logsDirectoryPath, "_Event.2026-08.txt");

        var candidates = new EventLogReconciliationScanner().Scan(workspace.PlayersDirectoryPath);

        var candidate = Assert.Single(candidates);
        Assert.Equal("Alice", candidate.ProfileName);
    }

    [Fact]
    public void SupportsMultipleProfiles()
    {
        using var workspace = new TemporaryWorkspace();
        var aliceLogsDirectoryPath = workspace.CreateProfileLogsDirectory("Alice");
        var bobLogsDirectoryPath = workspace.CreateProfileLogsDirectory("Bob");
        var aliceFilePath = workspace.CreateFile(aliceLogsDirectoryPath, "_Event.2026-08.txt");
        var bobFilePath = workspace.CreateFile(bobLogsDirectoryPath, "_Event.2026-08-07.txt");

        var candidates = new EventLogReconciliationScanner().Scan(workspace.PlayersDirectoryPath);

        Assert.Contains(candidates, candidate =>
            candidate.ProfileName == "Alice"
            && candidate.EventFilePath == Path.GetFullPath(aliceFilePath));
        Assert.Contains(candidates, candidate =>
            candidate.ProfileName == "Bob"
            && candidate.EventFilePath == Path.GetFullPath(bobFilePath));
    }

    [Fact]
    public void ReturnsCreationTimeUtc()
    {
        using var workspace = new TemporaryWorkspace();
        var logsDirectoryPath = workspace.CreateProfileLogsDirectory("Alice");
        var eventFilePath = workspace.CreateFile(logsDirectoryPath, "_Event.2026-08.txt");
        var expectedCreationTimeUtc = File.GetCreationTimeUtc(eventFilePath);

        var candidate = Assert.Single(new EventLogReconciliationScanner().Scan(workspace.PlayersDirectoryPath));

        Assert.InRange(
            candidate.CreationTimeUtc,
            expectedCreationTimeUtc.AddSeconds(-2),
            expectedCreationTimeUtc.AddSeconds(2));
    }

    [Fact]
    public void SortsDeterministically()
    {
        using var workspace = new TemporaryWorkspace();
        var betaLogsDirectoryPath = workspace.CreateProfileLogsDirectory("beta");
        var alphaLogsDirectoryPath = workspace.CreateProfileLogsDirectory("Alpha");
        workspace.CreateFile(betaLogsDirectoryPath, "_Event.2026-08.txt");
        workspace.CreateFile(alphaLogsDirectoryPath, "_Event.2026-08-31.txt");
        workspace.CreateFile(alphaLogsDirectoryPath, "_Event.2026-08-01.txt");

        var candidates = new EventLogReconciliationScanner().Scan(workspace.PlayersDirectoryPath);

        Assert.Equal(
            ["Alpha/_Event.2026-08-01.txt", "Alpha/_Event.2026-08-31.txt", "beta/_Event.2026-08.txt"],
            candidates.Select(candidate =>
                $"{candidate.ProfileName}/{Path.GetFileName(candidate.EventFilePath)}"));
    }

    [Fact]
    public void PathsAreFullAndNormalized()
    {
        using var workspace = new TemporaryWorkspace();
        var logsDirectoryPath = workspace.CreateProfileLogsDirectory("Alice");
        var eventFilePath = workspace.CreateFile(logsDirectoryPath, "_Event.2026-08.txt");
        var playersPathWithRelativeSegments = Path.Combine(
            workspace.PlayersDirectoryPath,
            "..",
            "players",
            ".");

        var candidate = Assert.Single(new EventLogReconciliationScanner().Scan(playersPathWithRelativeSegments));

        Assert.Equal(Path.GetFullPath(eventFilePath), candidate.EventFilePath);
        Assert.True(Path.IsPathFullyQualified(candidate.EventFilePath));
        Assert.DoesNotContain("..", candidate.EventFilePath);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"WurmMiningCounter.Tests-{Guid.NewGuid():N}");

        public TemporaryWorkspace()
        {
            PlayersDirectoryPath = Path.Combine(_directoryPath, "wurm", "players");
            Directory.CreateDirectory(PlayersDirectoryPath);
        }

        public string PlayersDirectoryPath { get; }

        public string CreateProfileDirectory(string profileName)
        {
            var profileDirectoryPath = Path.Combine(PlayersDirectoryPath, profileName);
            Directory.CreateDirectory(profileDirectoryPath);
            return profileDirectoryPath;
        }

        public string CreateProfileLogsDirectory(string profileName)
        {
            var logsDirectoryPath = Path.Combine(CreateProfileDirectory(profileName), "logs");
            Directory.CreateDirectory(logsDirectoryPath);
            return logsDirectoryPath;
        }

        public string CreateFile(string directoryPath, string fileName)
        {
            var filePath = Path.Combine(directoryPath, fileName);
            File.WriteAllText(filePath, "test");
            return filePath;
        }

        public void Dispose()
        {
            if (Directory.Exists(_directoryPath))
                Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
