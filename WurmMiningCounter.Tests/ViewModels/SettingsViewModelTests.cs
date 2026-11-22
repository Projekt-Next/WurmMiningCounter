using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void ConstructorWithNullSettingsThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsViewModel(null!));
    }

    [Fact]
    public void ConstructorUsesValuesFromSettings()
    {
        var viewModel = new SettingsViewModel(new AppSettings
        {
            AlwaysOnTop = true,
            StartMinimizedToTray = true
        });

        Assert.True(viewModel.AlwaysOnTop);
        Assert.True(viewModel.StartMinimizedToTray);
    }

    [Fact]
    public void ChangingAlwaysOnTopUpdatesValueAndRaisesPropertyChanged()
    {
        var viewModel = new SettingsViewModel(new AppSettings());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        viewModel.AlwaysOnTop = true;

        Assert.True(viewModel.AlwaysOnTop);
        Assert.Equal([nameof(SettingsViewModel.AlwaysOnTop)], changedProperties);
    }

    [Fact]
    public void ChangingStartMinimizedToTrayUpdatesValueAndRaisesPropertyChanged()
    {
        var viewModel = new SettingsViewModel(new AppSettings());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        viewModel.StartMinimizedToTray = true;

        Assert.True(viewModel.StartMinimizedToTray);
        Assert.Equal([nameof(SettingsViewModel.StartMinimizedToTray)], changedProperties);
    }

    [Fact]
    public void SettingExistingValuesDoesNotRaisePropertyChanged()
    {
        var viewModel = new SettingsViewModel(new AppSettings());
        var changeCount = 0;
        viewModel.PropertyChanged += (_, _) => changeCount++;

        viewModel.AlwaysOnTop = false;
        viewModel.StartMinimizedToTray = false;

        Assert.Equal(0, changeCount);
    }

    [Fact]
    public void CreateSettingsReturnsCurrentValuesInNewIndependentInstances()
    {
        var source = new AppSettings
        {
            AlwaysOnTop = true,
            StartMinimizedToTray = true
        };
        var viewModel = new SettingsViewModel(source);

        var first = viewModel.CreateSettings();
        var second = viewModel.CreateSettings();
        first.AlwaysOnTop = false;

        Assert.NotSame(source, first);
        Assert.NotSame(first, second);
        Assert.True(second.AlwaysOnTop);
        Assert.True(second.StartMinimizedToTray);
    }

    [Fact]
    public void CreateSettingsPreservesAdditionalWurmDataLocations()
    {
        var viewModel = new SettingsViewModel(new AppSettings
        {
            AdditionalWurmDataLocations = [@"D:\\WurmData"]
        });

        var settings = viewModel.CreateSettings();

        Assert.Equal([@"D:\\WurmData"], settings.AdditionalWurmDataLocations);
    }

    [Fact]
    public void ChangingKnownLocationEnabledPreservesAllOtherRuleFields()
    {
        var source = new KnownLogLocation
        {
            Id = "steam-rule",
            Enabled = true,
            Game = "WurmUnlimited",
            Type = KnownLogLocations.SteamType,
            AppId = 366220,
            PlayersRelativePath = @"WurmLauncher\PlayerFiles\players"
        };
        var viewModel = new SettingsViewModel(new AppSettings { KnownLogLocations = [source] });

        Assert.Single(viewModel.KnownLogLocations).Enabled = false;

        var saved = Assert.Single(viewModel.CreateSettings().KnownLogLocations);
        Assert.False(saved.Enabled);
        Assert.Equal(source.Id, saved.Id);
        Assert.Equal(source.Game, saved.Game);
        Assert.Equal(source.Type, saved.Type);
        Assert.Equal(source.AppId, saved.AppId);
        Assert.Equal(source.PlayersRelativePath, saved.PlayersRelativePath);
    }

    [Fact]
    public void AdditionalLocationAddAndRemovePublishSettingsChangeAndOnlyAffectManualLocations()
    {
        var viewModel = new SettingsViewModel(new AppSettings
        {
            KnownLogLocations = [new KnownLogLocation { Id = "direct", Enabled = true, Game = "WurmOnline", Type = KnownLogLocations.DirectType, Path = @"C:\\Wurm\\players" }]
        });
        var changes = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(SettingsViewModel.LocationSettingsChanged))
                changes++;
        };

        Assert.True(viewModel.AddAdditionalWurmDataLocation(@" D:\\WurmData "));
        Assert.False(viewModel.AddAdditionalWurmDataLocation(@"d:\\wurmdata"));
        Assert.True(viewModel.RemoveAdditionalWurmDataLocation(@"D:\\WurmData"));

        var saved = viewModel.CreateSettings();
        Assert.Empty(saved.AdditionalWurmDataLocations);
        Assert.Equal("direct", Assert.Single(saved.KnownLogLocations).Id);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void RemovingSelectedAdditionalLocationUpdatesTheBoundCollectionImmediatelyAndClearsSelection()
    {
        var viewModel = new SettingsViewModel(new AppSettings
        {
            AdditionalWurmDataLocations = [@"D:\\First", @"D:\\Second"]
        });

        viewModel.SelectedAdditionalWurmDataLocation = @"D:\\First";

        Assert.True(viewModel.RemoveSelectedAdditionalWurmDataLocation());
        Assert.Equal([@"D:\\Second"], viewModel.AdditionalWurmDataLocations);
        Assert.Null(viewModel.SelectedAdditionalWurmDataLocation);
        Assert.False(viewModel.CanRemoveAdditionalWurmDataLocation);
        Assert.False(viewModel.RemoveSelectedAdditionalWurmDataLocation());
    }

    [Fact]
    public void RemovingLastSelectedAdditionalLocationRetainsAnIntentionalEmptyListForPersistence()
    {
        var viewModel = new SettingsViewModel(new AppSettings
        {
            AdditionalWurmDataLocations = [@"D:\\Only"]
        });

        viewModel.SelectedAdditionalWurmDataLocation = @"D:\\Only";

        Assert.True(viewModel.RemoveSelectedAdditionalWurmDataLocation());
        var saved = viewModel.CreateSettings();
        Assert.Empty(saved.AdditionalWurmDataLocations);
        Assert.True(saved.LocationSettingsChanged);
    }

    [Fact]
    public void RemovingSelectedAdditionalLocationPersistsWithoutChangingKnownLocationsOrRunningDiscovery()
    {
        var current = new AppSettings
        {
            AdditionalWurmDataLocations = [@"D:\\Manual"],
            KnownLogLocations = [new KnownLogLocation
            {
                Id = "online",
                Enabled = true,
                Game = "WurmOnline",
                Type = KnownLogLocations.DirectType,
                Path = @"C:\\Wurm\\players"
            }]
        };
        var viewModel = new SettingsViewModel(current);
        viewModel.SelectedAdditionalWurmDataLocation = @"D:\\Manual";

        Assert.True(viewModel.RemoveSelectedAdditionalWurmDataLocation());
        Assert.True(App.TryApplySettingsChanges(current, viewModel.CreateSettings(), _ => true));

        Assert.Empty(current.AdditionalWurmDataLocations);
        Assert.True(Assert.Single(current.KnownLogLocations).Enabled);
        Assert.Equal("online", Assert.Single(current.KnownLogLocations).Id);
    }

    [Fact]
    public async Task FindLocationsPublishesSummariesAndAllDiagnosticKinds()
    {
        var result = CreateDiscoveryResult();
        var viewModel = new SettingsViewModel(new AppSettings(), null, (_, _) => result);

        await viewModel.FindLocationsAsync();

        var root = Assert.Single(viewModel.DiscoveredRoots);
        var profile = Assert.Single(root.Profiles);
        Assert.Equal(1, viewModel.DiscoveryRootCount);
        Assert.Equal(1, viewModel.DiscoveryProfileCount);
        Assert.Equal(1, viewModel.DiscoveryEventFileCount);
        Assert.Equal(1, viewModel.DiscoverySkillsFileCount);
        Assert.Equal(1, root.EventFileCount);
        Assert.Equal(1, root.SkillsFileCount);
        Assert.Equal("same-name", profile.ProfileName);
        Assert.Equal(5, viewModel.LocationDiscoveryDiagnostics.Count);
        Assert.Contains(viewModel.LocationDiscoveryDiagnostics, item => item.StartsWith("Steam:"));
        Assert.Contains(viewModel.LocationDiscoveryDiagnostics, item => item.StartsWith("Location:"));
        Assert.Contains(viewModel.LocationDiscoveryDiagnostics, item => item.StartsWith("Profile name conflict:"));
        Assert.Contains(viewModel.LocationDiscoveryDiagnostics, item => item.StartsWith("Source ownership ambiguity"));
        Assert.Contains(viewModel.LocationDiscoveryDiagnostics, item => item.StartsWith("Physical identity unavailable"));
        Assert.Equal("4 warnings, 1 error.", viewModel.DiscoveryDiagnosticSummaryText);
        Assert.Equal("Discovery completed.", viewModel.LocationDiscoveryStatusText);
    }

    [Fact]
    public async Task IncompleteDiscoveryShowsPartialStatusAndRetainsEarlierDetectedRule()
    {
        var known = new KnownLogLocation
        {
            Id = "online", Enabled = true, Game = "WurmOnline",
            Type = KnownLogLocations.DirectType, Path = @"C:\\Wurm\\players"
        };
        var root = new WurmLogDataRoot(@"C:\\Wurm\\players", [])
        {
            Origins = [new WurmLogLocationOrigin(@"C:\\Wurm\\players", "online", "WurmOnline", false)]
        };
        var complete = new WurmLogLocationResult([root], [], [], [], []);
        var incomplete = new WurmLogLocationResult([], [new WurmLogLocationRejection(@"C:\\Denied", WurmLogLocationRejectionReason.AccessDenied)], [], [], [])
        {
            IsComplete = false
        };
        var calls = 0;
        var viewModel = new SettingsViewModel(new AppSettings { KnownLogLocations = [known] }, null,
            (_, _) => Interlocked.Increment(ref calls) == 1 ? complete : incomplete);

        await viewModel.FindLocationsAsync();
        await viewModel.FindLocationsAsync();

        Assert.Equal("Discovery incomplete; showing verified partial results.", viewModel.LocationDiscoveryStatusText);
        Assert.NotEqual("Discovery completed.", viewModel.LocationDiscoveryStatusText);
        Assert.Equal("Detected", Assert.Single(viewModel.KnownLogLocations).DiscoveryStatus);
        Assert.Empty(viewModel.DiscoveredRoots);
        Assert.Equal(0, viewModel.DiscoveryRootCount);
    }

    [Fact]
    public async Task FindLocationsIgnoresRepeatedRequestWhileRunIsInProgress()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var viewModel = new SettingsViewModel(new AppSettings(), null, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Wait();
            return CreateDiscoveryResult();
        });

        var first = viewModel.FindLocationsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = viewModel.FindLocationsAsync();
        release.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.False(viewModel.IsLocationDiscoveryRunning);
        Assert.Single(viewModel.DiscoveredRoots);
    }

    [Fact]
    public async Task CancelLocationDiscoveryDoesNotPublishStaleResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var viewModel = new SettingsViewModel(new AppSettings(), null, (_, _) =>
        {
            entered.TrySetResult();
            release.Wait();
            return CreateDiscoveryResult();
        });

        var task = viewModel.FindLocationsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.CancelLocationDiscovery();
        release.Set();
        await task;

        Assert.False(viewModel.IsLocationDiscoveryRunning);
        Assert.Empty(viewModel.DiscoveredRoots);
        Assert.Equal("Finding log locations…", viewModel.LocationDiscoveryStatusText);
    }

    [Fact]
    public async Task LocationConfigurationChangeDuringScanClearsAndInvalidatesResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var viewModel = new SettingsViewModel(new AppSettings(), null, (_, _) =>
        {
            entered.TrySetResult();
            release.Wait();
            return CreateDiscoveryResult();
        });

        var task = viewModel.FindLocationsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.AddAdditionalWurmDataLocation(@"D:\\NewRoot"));
        release.Set();
        await task;

        Assert.Empty(viewModel.DiscoveredRoots);
        Assert.Equal("Location settings changed. Run Find locations to refresh.", viewModel.LocationDiscoveryStatusText);
    }

    [Fact]
    public async Task DiscoveryExceptionPublishesFailureWithoutLeavingRunActive()
    {
        var viewModel = new SettingsViewModel(new AppSettings(), null, (_, _) => throw new IOException("test failure"));

        await viewModel.FindLocationsAsync();

        Assert.False(viewModel.IsLocationDiscoveryRunning);
        Assert.Equal("Finding locations failed.", viewModel.LocationDiscoveryStatusText);
        Assert.Equal("Discovery failed: test failure", Assert.Single(viewModel.LocationDiscoveryDiagnostics));
    }

    [Fact]
    public void SuccessfulLocationSaveResetsDirtyStateForLaterGeneralAutosave()
    {
        var current = new AppSettings { AdditionalWurmDataLocations = [@"D:\\Initial"] };
        var viewModel = new SettingsViewModel(current);
        Assert.True(viewModel.AddAdditionalWurmDataLocation(@"D:\\UserAdded"));
        Assert.True(viewModel.CreateSettings().LocationSettingsChanged);

        Assert.True(App.TryApplySettingsChanges(current, viewModel.CreateSettings(), _ => true));
        viewModel.MarkLocationSettingsSaved();
        current.AdditionalWurmDataLocations.Add(@"D:\\External");
        viewModel.AlwaysOnTop = true;

        var autosave = viewModel.CreateSettings();
        Assert.False(autosave.LocationSettingsChanged);
        Assert.True(App.TryApplySettingsChanges(current, autosave, _ => true));
        Assert.Equal([@"D:\\Initial", @"D:\\UserAdded", @"D:\\External"], current.AdditionalWurmDataLocations);
    }

    [Fact]
    public void LocationEditMergesWithExternalUpdateFromAnOpenSettingsSnapshot()
    {
        var current = new AppSettings { AdditionalWurmDataLocations = [@"D:\\Initial"] };
        var viewModel = new SettingsViewModel(current);
        current.AdditionalWurmDataLocations.Add(@"D:\\External");
        Assert.True(viewModel.AddAdditionalWurmDataLocation(@"D:\\UserAdded"));

        Assert.True(App.TryApplySettingsChanges(current, viewModel.CreateSettings(), _ => true));

        Assert.Equal([@"D:\\Initial", @"D:\\External", @"D:\\UserAdded"], current.AdditionalWurmDataLocations);
    }

    [Fact]
    public void IntentionalRemovalOfLastManualLocationPersistsAnEmptyList()
    {
        var current = new AppSettings { AdditionalWurmDataLocations = [@"D:\\Initial"] };
        var viewModel = new SettingsViewModel(current);
        Assert.True(viewModel.RemoveAdditionalWurmDataLocation(@"D:\\Initial"));

        Assert.True(App.TryApplySettingsChanges(current, viewModel.CreateSettings(), _ => true));

        Assert.Empty(current.AdditionalWurmDataLocations);
    }

    [Fact]
    public void FailedLocationSaveLeavesTheEditDirtyAndReopenedSettingsUsePersistedValues()
    {
        var current = new AppSettings { AdditionalWurmDataLocations = [@"D:\\Initial"] };
        var viewModel = new SettingsViewModel(current);
        Assert.True(viewModel.AddAdditionalWurmDataLocation(@"D:\\Unsaved"));

        Assert.False(App.TryApplySettingsChanges(current, viewModel.CreateSettings(), _ => false));
        Assert.True(viewModel.CreateSettings().LocationSettingsChanged);
        Assert.Equal([@"D:\\Initial"], new SettingsViewModel(current).AdditionalWurmDataLocations);
    }

    [Fact]
    public async Task DiscoveredRootsKeepGameOriginsAndSameNamedProfilesSeparate()
    {
        var onlineProfile = new WurmLogProfileLocation("same-name", @"C:\\online\\logs");
        var unlimitedProfile = new WurmLogProfileLocation("same-name", @"D:\\unlimited\\logs");
        var result = new WurmLogLocationResult(
            [
                new WurmLogDataRoot(@"C:\\online\\players", [onlineProfile]) { Origins = [new WurmLogLocationOrigin(@"C:\\online\\players", "online", "WurmOnline", false)] },
                new WurmLogDataRoot(@"D:\\unlimited\\players", [unlimitedProfile]) { Origins = [new WurmLogLocationOrigin(@"D:\\unlimited\\players", "unlimited", "WurmUnlimited", false)] }
            ], [], [], [], []);
        var viewModel = new SettingsViewModel(new AppSettings(), null, (_, _) => result);

        await viewModel.FindLocationsAsync();

        Assert.Equal(2, viewModel.DiscoveredRoots.Count);
        Assert.Equal(["WurmOnline", "WurmUnlimited"], viewModel.DiscoveredRoots.SelectMany(root => root.Origins).Select(origin => origin.Game).Order());
        Assert.Equal(["WurmOnline (online)", "WurmUnlimited (unlimited)"], viewModel.DiscoveredRoots.Select(root => root.SourceSummary).Order());
        Assert.Equal(2, viewModel.DiscoveredRoots.SelectMany(root => root.Profiles).Count(profile => profile.ProfileName == "same-name"));
    }

    [Fact]
    public async Task KnownLocationPresentationUsesConfirmedRuleOriginAndDetectedPlayersPath()
    {
        var playersDirectory = @"C:\\Wurm\\players";
        var root = new WurmLogDataRoot(playersDirectory, [])
        {
            Origins = [new WurmLogLocationOrigin(playersDirectory, "wurm-online-standard", "WurmOnline", false)]
        };
        var result = new WurmLogLocationResult([root], [], [], [], []);
        var viewModel = new SettingsViewModel(
            new AppSettings { KnownLogLocations = KnownLogLocations.CreateDefaults() },
            null,
            (_, _) => result);

        await viewModel.FindLocationsAsync();

        var standard = Assert.Single(viewModel.KnownLogLocations, location => location.Id == "wurm-online-standard");
        Assert.Equal("Wurm Online Standard", standard.DisplayName);
        Assert.Equal("Detected", standard.DiscoveryStatus);
        Assert.Equal([playersDirectory], standard.DetectedPlayersDirectories);
        Assert.Equal("Not detected", Assert.Single(viewModel.KnownLogLocations, location => location.Id == "wurm-online-steam").DiscoveryStatus);
    }

    [Fact]
    public void SnapshotPublishedAfterSettingsOpensUpdatesDiscoveryPresentation()
    {
        var registryDirectory = Path.Combine(Path.GetTempPath(), "WurmMiningCounter-SettingsSnapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = CreateSnapshotService(registryDirectory, out _);
            using var viewModel = new SettingsViewModel(new AppSettings { KnownLogLocations = [SnapshotLocation()] }, null, service, action => action());

            var snapshot = service.Discover([SnapshotLocation()], []);

            Assert.Same(snapshot, viewModel.LastDiscoverySnapshot);
            Assert.Equal(1, viewModel.DiscoveryRootCount);
            Assert.Equal(1, viewModel.DiscoveryProfileCount);
            Assert.Equal(1, viewModel.DiscoveryEventFileCount);
            Assert.Equal(1, viewModel.DiscoverySkillsFileCount);
            Assert.Equal("Discovery completed.", viewModel.LocationDiscoveryStatusText);
            Assert.Equal("Detected", Assert.Single(viewModel.KnownLogLocations).DiscoveryStatus);
        }
        finally
        {
            if (Directory.Exists(registryDirectory)) Directory.Delete(registryDirectory, true);
        }
    }

    [Fact]
    public async Task SettingsOpenedAfterStartupSnapshotUsesItAndManualFindRescansTheSameService()
    {
        var registryDirectory = Path.Combine(Path.GetTempPath(), "WurmMiningCounter-SettingsSnapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = CreateSnapshotService(registryDirectory, out var locateCount);
            var startupSnapshot = service.Discover([SnapshotLocation()], []);

            using var viewModel = new SettingsViewModel(new AppSettings { KnownLogLocations = [SnapshotLocation()] }, null, service, action => action());

            Assert.Same(startupSnapshot, viewModel.LastDiscoverySnapshot);
            Assert.Equal(1, locateCount());
            Assert.Equal(1, viewModel.DiscoveryRootCount);

            await viewModel.FindLocationsAsync();

            Assert.Equal(2, locateCount());
            Assert.Same(service.GetCurrentSnapshot()!.Snapshot, viewModel.LastDiscoverySnapshot);
            Assert.Equal(1, viewModel.DiscoveryProfileCount);
            Assert.Equal("Discovery completed.", viewModel.LocationDiscoveryStatusText);
        }
        finally
        {
            if (Directory.Exists(registryDirectory)) Directory.Delete(registryDirectory, true);
        }
    }

    [Fact]
    public void DelayedStartupSnapshotDoesNotOverwriteNewerLocationConfiguration()
    {
        var registryDirectory = Path.Combine(Path.GetTempPath(), "WurmMiningCounter-SettingsSnapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = CreateSnapshotService(registryDirectory, out _);
            var queued = new Queue<Action>();
            using var viewModel = new SettingsViewModel(
                new AppSettings { KnownLogLocations = [SnapshotLocation()] },
                null,
                service,
                action => queued.Enqueue(action));

            service.Discover([SnapshotLocation()], []);
            Assert.True(viewModel.AddAdditionalWurmDataLocation(@"D:\Manual"));
            Assert.Single(queued).Invoke();

            Assert.Empty(viewModel.DiscoveredRoots);
            Assert.Equal("Location settings changed. Run Find locations to refresh.", viewModel.LocationDiscoveryStatusText);
        }
        finally
        {
            if (Directory.Exists(registryDirectory)) Directory.Delete(registryDirectory, true);
        }
    }

    [Fact]
    public void OlderQueuedSnapshotCannotOverwriteANewerSnapshot()
    {
        var registryDirectory = Path.Combine(Path.GetTempPath(), "WurmMiningCounter-SettingsSnapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = CreateSnapshotService(registryDirectory, out _);
            var queued = new List<Action>();
            using var viewModel = new SettingsViewModel(
                new AppSettings { KnownLogLocations = [SnapshotLocation()] },
                null,
                service,
                action => queued.Add(action));

            service.Discover([SnapshotLocation()], []);
            var newerSnapshot = service.Discover([SnapshotLocation()], []);
            var published = queued.ToArray();
            Assert.Equal(2, published.Length);

            published[1]();
            published[0]();

            Assert.Same(newerSnapshot, viewModel.LastDiscoverySnapshot);
        }
        finally
        {
            if (Directory.Exists(registryDirectory)) Directory.Delete(registryDirectory, true);
        }
    }

    [Fact]
    public void DiagnosticsStatusTextTracksEnabledStateImmediately()
    {
        var viewModel = new SettingsViewModel(new AppSettings { DiagnosticsEnabled = true });
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        Assert.Equal("Enabled", viewModel.DiagnosticsStatusText);

        viewModel.DiagnosticsEnabled = false;

        Assert.Equal("Disabled", viewModel.DiagnosticsStatusText);
        Assert.Equal(
            [nameof(SettingsViewModel.DiagnosticsEnabled), nameof(SettingsViewModel.DiagnosticsStatusText)],
            changedProperties);
    }

    [Fact]
    public async Task LocationDiagnosticsDescribeMissingProfileLogsWithoutExposingItsPath()
    {
        var result = new WurmLogLocationResult([], [
            new WurmLogLocationRejection(@"C:\\missing-players", WurmLogLocationRejectionReason.Missing),
            new WurmLogLocationRejection(@"C:\\players\\profile", WurmLogLocationRejectionReason.ProfileLogsFolderMissing)
        ], [], [], []);
        var viewModel = new SettingsViewModel(new AppSettings(), null, (_, _) => result);

        await viewModel.FindLocationsAsync();

        Assert.Contains("Location: Missing", viewModel.LocationDiscoveryDiagnostics);
        Assert.Contains("Profile logs folder missing", viewModel.LocationDiscoveryDiagnostics);
        Assert.DoesNotContain(viewModel.LocationDiscoveryDiagnostics, item => item.Contains(@"C:\\", StringComparison.OrdinalIgnoreCase));
    }

    private static KnownLogLocation SnapshotLocation() => new()
    {
        Id = "snapshot", Enabled = true, Game = "Test", Type = KnownLogLocations.DirectType, Path = @"C:\Snapshot\players"
    };

    private static LogDiscoverySnapshotService CreateSnapshotService(string registryDirectory, out Func<int> locateCount)
    {
        const string players = @"C:\Snapshot\players";
        const string profile = @"C:\Snapshot\players\Alice";
        const string logs = @"C:\Snapshot\players\Alice\logs";
        var calls = 0;
        locateCount = () => Volatile.Read(ref calls);
        var identity = new DelegateDirectoryPhysicalIdentityProvider(_ => "physical:alice");
        var locator = new WurmLogLocationLocator(
            _ => true,
            path =>
            {
                if (path == players)
                    Interlocked.Increment(ref calls);
                return path == players ? [profile] : [];
            },
            path => path,
            identity,
            () => "unused",
            _ => [],
            _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []),
            _ => [Path.Combine(logs, "_Event.2026-10.txt"), Path.Combine(logs, "_Skills.2026-10.txt")],
            _ => WurmLogDirectoryProbeResult.Exists);
        return new LogDiscoverySnapshotService(locator, new PlayerProfileIdentityResolver(
            Path.Combine(registryDirectory, "registry"), identity));
    }

    [Fact]
    public void RefreshDiagnosticsStatePublishesCurrentEntryCount()
    {
        var diagnostics = new RuntimeDiagnosticService();
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "One");
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Two");
        var viewModel = new SettingsViewModel(new AppSettings(), diagnostics);

        viewModel.RefreshDiagnosticsState();

        Assert.Equal(2, viewModel.DiagnosticEntryCount);
        Assert.Equal("Entries: 2 / 1000", viewModel.DiagnosticEntryCountText);

        diagnostics.Clear();
        viewModel.RefreshDiagnosticsState();

        Assert.Equal(0, viewModel.DiagnosticEntryCount);
        Assert.Equal("Entries: 0 / 1000", viewModel.DiagnosticEntryCountText);
    }

    private static WurmLogLocationResult CreateDiscoveryResult()
    {
        var profile = new WurmLogProfileLocation("same-name", @"C:\\logs")
        {
            Files =
            [
                new WurmLogFileLocation(WurmLogFileKind.Event, @"C:\\logs\\_Event.2026-10.txt", @"C:\\players", "same-name", @"C:\\logs"),
                new WurmLogFileLocation(WurmLogFileKind.Skills, @"C:\\logs\\_Skills.2026-10.txt", @"C:\\players", "same-name", @"C:\\logs")
            ]
        };
        var root = new WurmLogDataRoot(@"C:\\players", [profile]);
        return new WurmLogLocationResult(
            [root],
            [new WurmLogLocationRejection(@"C:\\missing", WurmLogLocationRejectionReason.Missing)],
            [new WurmLogProfileNameConflict("same-name", [profile])],
            [new SteamDiscoveryDiagnostic("Steam", SteamDiscoveryIssue.SteamNotFound)],
            [new WurmLogLocationUncertainty(@"C:\\logs", WurmLogLocationUncertaintyReason.PhysicalIdentityUnavailable)])
        {
            SourceOwnershipAmbiguities = [new WurmLogSourceOwnershipAmbiguity([new WurmLogSourceClaim(@"C:\\players", profile)])],
            Files = profile.Files
        };
    }
}
