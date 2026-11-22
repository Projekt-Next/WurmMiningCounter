using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class SteamInstallationLocatorTests
{
    [Fact]
    public void FindsBothGamesAcrossLegacyLibrariesAndReadsEachMetadataFileOnce()
    {
        using var files = new SteamFiles(@"C:\Steam", @"D:\SteamLibrary");
        files.AddVdf("""
            "LibraryFolders"
            {
                "1" "D:\\SteamLibrary"
            }
            """);
        files.AddManifest(files.Root, 1179680, "Wurm Online");
        files.AddManifest(files.SecondLibrary, 366220, "Wurm Unlimited");

        var result = files.CreateLocator().LocateMany([1179680, 366220]);

        Assert.Equal([files.Installation(files.Root, "Wurm Online")], result.InstallationsByAppId[1179680]);
        Assert.Equal([files.Installation(files.SecondLibrary, "Wurm Unlimited")], result.InstallationsByAppId[366220]);
        Assert.Equal(1, files.ReadCount(files.VdfPath));
        Assert.Equal(1, files.ReadCount(files.ManifestPath(files.Root, 1179680)));
        Assert.Equal(1, files.ReadCount(files.ManifestPath(files.SecondLibrary, 366220)));
    }

    [Fact]
    public void CurrentVdfIgnoresNestedAppsAndEscapedLibraryPath()
    {
        using var files = new SteamFiles(@"C:\Steam", @"D:\Steam Library");
        files.AddVdf("""
            // Current Steam format
            "libraryfolders"
            {
                "0"
                {
                    "path" "C:\\Steam"
                    "apps" { "1179680" "123" }
                }
                "1"
                {
                    "path" "D:\\Steam Library"
                    "apps" { "366220" "456" }
                }
            }
            """);
        files.AddManifest(files.SecondLibrary, 366220, "Wurm Unlimited");

        var result = files.CreateLocator().LocateMany([366220]);

        Assert.Equal([files.Installation(files.SecondLibrary, "Wurm Unlimited")], result.InstallationsByAppId[366220]);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ReportsMalformedVdfWithoutAcceptingPartialLibraries()
    {
        using var files = new SteamFiles(@"E:\CustomSteam", @"F:\SteamLibrary");
        files.AddVdf("\"libraryfolders\" { \"1\" \"F:\\\\SteamLibrary\"");
        files.AddManifest(files.SecondLibrary, 1179680, "Wurm Online");

        var result = files.CreateLocator().LocateMany([1179680]);

        Assert.Empty(result.InstallationsByAppId[1179680]);
        Assert.Contains(result.Diagnostics, item => item.Path == files.VdfPath && item.Issue == SteamDiscoveryIssue.MetadataInvalid);
    }

    [Fact]
    public void ReportsUnavailableLibraryAndManifestAccessDenied()
    {
        using var files = new SteamFiles(@"C:\Steam", @"D:\OfflineLibrary");
        files.AddVdf("\"libraryfolders\" { \"1\" \"D:\\\\OfflineLibrary\" }");
        files.SetDirectoryExists(files.SecondLibrary, false);
        files.AddManifest(files.Root, 1179680, "Wurm Online");
        files.ThrowOnRead(files.ManifestPath(files.Root, 1179680), new UnauthorizedAccessException());

        var result = files.CreateLocator().LocateMany([1179680]);

        Assert.Empty(result.InstallationsByAppId[1179680]);
        Assert.Contains(result.Diagnostics, item => item.Path == files.SecondLibrary && item.Issue == SteamDiscoveryIssue.LibraryUnavailable);
        Assert.Contains(result.Diagnostics, item => item.Path == files.ManifestPath(files.Root, 1179680) && item.Issue == SteamDiscoveryIssue.AccessDenied);
    }

    [Fact]
    public void ReportsMalformedManifestAndLeavesMissingManifestAsNotInstalled()
    {
        using var files = new SteamFiles(@"C:\Steam");
        files.AddRaw(files.ManifestPath(files.Root, 1179680), "\"AppState\" { \"appid\" \"1179680\"");

        var malformed = files.CreateLocator().LocateMany([1179680, 366220]);

        Assert.Empty(malformed.InstallationsByAppId[1179680]);
        Assert.Empty(malformed.InstallationsByAppId[366220]);
        Assert.Contains(malformed.Diagnostics, item => item.Path == files.ManifestPath(files.Root, 1179680) && item.Issue == SteamDiscoveryIssue.MetadataInvalid);
        Assert.DoesNotContain(malformed.Diagnostics, item => item.Path == files.ManifestPath(files.Root, 366220));
    }

    [Theory]
    [InlineData("366220", "Wurm Online")]
    [InlineData("1179680", "..\\escape")]
    [InlineData("1179680", "C:\\escape")]
    public void RejectsManifestWithWrongAppIdOrUnsafeInstallDirectory(string manifestAppId, string installDirectory)
    {
        using var files = new SteamFiles(@"C:\Steam");
        files.AddManifest(files.Root, 1179680, installDirectory, manifestAppId);

        var result = files.CreateLocator().LocateMany([1179680]);

        Assert.Empty(result.InstallationsByAppId[1179680]);
        Assert.Contains(result.Diagnostics, item => item.Path == files.ManifestPath(files.Root, 1179680) && item.Issue == SteamDiscoveryIssue.MetadataInvalid);
    }

    [Fact]
    public void ReportsSteamNotFoundWhenNoConfiguredOrFallbackRootExists()
    {
        var locator = new SteamInstallationLocator(() => [@"Z:\NoSteam"], _ => false, _ => false, _ => 0, _ => "");

        var result = locator.LocateMany([1179680]);

        Assert.Empty(result.InstallationsByAppId[1179680]);
        Assert.Contains(result.Diagnostics, item => item.Issue == SteamDiscoveryIssue.SteamNotFound);
    }

    [Fact]
    public void RejectsVdfThatExceedsTheNestingLimit()
    {
        using var files = new SteamFiles(@"C:\Steam");
        files.AddVdf(string.Concat(Enumerable.Range(0, 65).Select(index => $"\"{index}\" {{ ")) + new string('}', 65));

        var result = files.CreateLocator().LocateMany([1179680]);

        Assert.Contains(result.Diagnostics, item => item.Path == files.VdfPath && item.Issue == SteamDiscoveryIssue.MetadataInvalid);
    }

    [Fact]
    public void UsesConfigLibraryFoldersWhenStandardFileIsMissing()
    {
        using var files = new SteamFiles(@"C:\Steam", @"D:\Library");
        var legacy = Path.Combine(files.Root, "config", "libraryfolders.vdf");
        files.AddRaw(legacy, "\"libraryfolders\" { \"1\" \"D:\\\\Library\" }");
        files.AddManifest(files.SecondLibrary, 1179680, "Wurm Online");

        var result = files.CreateLocator().LocateMany([1179680]);

        Assert.Equal([files.Installation(files.SecondLibrary, "Wurm Online")], result.InstallationsByAppId[1179680]);
        Assert.Equal(1, files.ReadCount(legacy));
    }

    [Fact]
    public void ReadsBothLibraryFolderSourcesAndDeduplicatesSharedLibraries()
    {
        using var files = new SteamFiles(@"C:\Steam", @"D:\Library");
        var config = Path.Combine(files.Root, "config", "libraryfolders.vdf");
        files.AddVdf("\"libraryfolders\" { \"1\" \"D:\\\\Library\" }");
        files.AddRaw(config, "\"libraryfolders\" { \"1\" \"D:\\\\Library\" }");
        files.AddManifest(files.SecondLibrary, 1179680, "Wurm Online");

        var result = files.CreateLocator().LocateMany([1179680]);

        Assert.Equal([files.Installation(files.SecondLibrary, "Wurm Online")], result.InstallationsByAppId[1179680]);
        Assert.Equal(1, files.ReadCount(files.VdfPath));
        Assert.Equal(1, files.ReadCount(config));
        Assert.Equal(1, files.ReadCount(files.ManifestPath(files.SecondLibrary, 1179680)));
    }

    [Fact]
    public void MergesDifferentLibrariesFromBothLibraryFolderSources()
    {
        using var files = new SteamFiles(@"C:\Steam", @"D:\Library");
        var thirdLibrary = Path.GetFullPath(@"E:\Library");
        var config = Path.Combine(files.Root, "config", "libraryfolders.vdf");
        files.AddVdf("\"libraryfolders\" { \"1\" \"D:\\\\Library\" }");
        files.AddRaw(config, "\"libraryfolders\" { \"2\" \"E:\\\\Library\" }");
        files.SetDirectoryExists(thirdLibrary, true);
        files.AddManifest(files.SecondLibrary, 1179680, "Wurm Online");
        files.AddManifest(thirdLibrary, 366220, "Wurm Unlimited");

        var result = files.CreateLocator().LocateMany([1179680, 366220]);

        Assert.Equal([files.Installation(files.SecondLibrary, "Wurm Online")], result.InstallationsByAppId[1179680]);
        Assert.Equal([files.Installation(thirdLibrary, "Wurm Unlimited")], result.InstallationsByAppId[366220]);
    }

    [Fact]
    public void KeepsBrokenSourceDiagnosticWhileUsingOtherValidLibraryFolderSource()
    {
        using var files = new SteamFiles(@"C:\Steam", @"D:\Library");
        var config = Path.Combine(files.Root, "config", "libraryfolders.vdf");
        files.AddVdf("\"libraryfolders\" { \"1\"");
        files.AddRaw(config, "\"libraryfolders\" { \"1\" \"D:\\\\Library\" }");
        files.AddManifest(files.SecondLibrary, 1179680, "Wurm Online");

        var result = files.CreateLocator().LocateMany([1179680]);

        Assert.Equal([files.Installation(files.SecondLibrary, "Wurm Online")], result.InstallationsByAppId[1179680]);
        Assert.Contains(result.Diagnostics, item => item.Path == files.VdfPath && item.Issue == SteamDiscoveryIssue.MetadataInvalid);
    }

    [Fact]
    public void KeepsUnavailableSourceDiagnosticWhileUsingOtherValidLibraryFolderSource()
    {
        using var files = new SteamFiles(@"C:\Steam", @"D:\Library");
        var config = Path.Combine(files.Root, "config", "libraryfolders.vdf");
        files.AddVdf("\"libraryfolders\" { \"1\" \"D:\\\\Library\" }");
        files.AddRaw(config, "\"libraryfolders\" { }");
        files.ThrowOnRead(config, new UnauthorizedAccessException());
        files.AddManifest(files.SecondLibrary, 1179680, "Wurm Online");

        var result = files.CreateLocator().LocateMany([1179680]);

        Assert.Equal([files.Installation(files.SecondLibrary, "Wurm Online")], result.InstallationsByAppId[1179680]);
        Assert.Contains(result.Diagnostics, item => item.Path == config && item.Issue == SteamDiscoveryIssue.AccessDenied);
    }

    [Fact]
    public void DistinguishesProbeAccessAndIoFailuresAndReadsAliasedLibraryManifestOnce()
    {
        var root = Path.GetFullPath(@"C:\Steam");
        var firstAlias = Path.GetFullPath(@"D:\AliasOne");
        var secondAlias = Path.GetFullPath(@"E:\AliasTwo");
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        var manifest = Path.Combine(firstAlias, "steamapps", "appmanifest_1179680.acf");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [vdf] = "\"libraryfolders\" { \"1\" \"D:\\\\AliasOne\" \"2\" \"E:\\\\AliasTwo\" }",
            [manifest] = "\"AppState\" { \"appid\" \"1179680\" \"installdir\" \"Wurm Online\" }"
        };
        var reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var locator = new SteamInstallationLocator(
            () => new SteamRootDiscovery([root], [new SteamDiscoveryDiagnostic("registry", SteamDiscoveryIssue.AccessDenied)]),
            path => path == Path.Combine(root, "steamapps", "appmanifest_366220.acf") ? SteamPathProbeResult.IoFailure : files.ContainsKey(path) ? SteamPathProbeResult.Exists : SteamPathProbeResult.Missing,
            path => path is var value && (value == root || value == firstAlias || value == secondAlias) ? SteamPathProbeResult.Exists : SteamPathProbeResult.Missing,
            path => files[path].Length,
            path => { reads[path] = reads.GetValueOrDefault(path) + 1; return files[path]; },
            path => path == firstAlias || path == secondAlias ? "same-library" : path);

        var result = locator.LocateMany([1179680, 366220]);

        Assert.Equal([Path.Combine(firstAlias, "steamapps", "common", "Wurm Online")], result.InstallationsByAppId[1179680]);
        Assert.Equal(1, reads[manifest]);
        Assert.Contains(result.Diagnostics, item => item.Path == "registry" && item.Issue == SteamDiscoveryIssue.AccessDenied);
        Assert.Contains(result.Diagnostics, item => item.Path == Path.Combine(root, "steamapps", "appmanifest_366220.acf") && item.Issue == SteamDiscoveryIssue.IoFailure);
    }

    private sealed class SteamFiles : IDisposable
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _reads = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _readFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

        public SteamFiles(string root, string? secondLibrary = null)
        {
            Root = Path.GetFullPath(root);
            SecondLibrary = secondLibrary is null ? string.Empty : Path.GetFullPath(secondLibrary);
            _directories.Add(Root);
            if (secondLibrary is not null)
                _directories.Add(SecondLibrary);
        }

        public string Root { get; }
        public string SecondLibrary { get; }
        public string VdfPath => Path.Combine(Root, "steamapps", "libraryfolders.vdf");
        public string ManifestPath(string library, int appId) => Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
        public string Installation(string library, string name) => Path.Combine(library, "steamapps", "common", name);

        public void AddVdf(string content) => _files[VdfPath] = content;
        public void AddRaw(string path, string content) => _files[path] = content;
        public void AddManifest(string library, int appId, string installDirectory, string? manifestAppId = null) =>
            _files[ManifestPath(library, appId)] = $"\"AppState\" {{ \"appid\" \"{manifestAppId ?? appId.ToString()}\" \"installdir\" \"{installDirectory.Replace("\\", "\\\\", StringComparison.Ordinal)}\" }}";
        public void SetDirectoryExists(string path, bool exists)
        {
            if (exists) _directories.Add(path); else _directories.Remove(path);
        }
        public void ThrowOnRead(string path, Exception exception) => _readFailures[path] = exception;
        public int ReadCount(string path) => _reads.GetValueOrDefault(path);

        public SteamInstallationLocator CreateLocator() => new(
            () => [Root], _files.ContainsKey, _directories.Contains,
            path => _files[path].Length,
            path =>
            {
                _reads[path] = ReadCount(path) + 1;
                if (_readFailures.TryGetValue(path, out var exception))
                    throw exception;
                return _files[path];
            });

        public void Dispose() { }
    }
}
