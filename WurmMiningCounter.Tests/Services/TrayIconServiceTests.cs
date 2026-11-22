using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Tests.Services;

[Collection("WPF")]
public sealed class TrayIconServiceTests
{
    [Fact]
    public void SettingsSaveResultControlsExistingUiStatusWithoutThrowing()
    {
        RunOnSta(() =>
        {
            var viewModel = new MainViewModel(new MiningSessionCounter());
            var mainWindow = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            using var service = new TrayIconService(
                mainWindow,
                viewModel,
                Dispatcher.CurrentDispatcher,
                (_, _, _) => new RecordingTrayIconResource(),
                () => (Icon)SystemIcons.Application.Clone(),
                settingsSaved: _ => false);
            var settingsViewModel = new SettingsViewModel(new AppSettings());

            var exception = Record.Exception(() => InvokePrivateMethod(
                service,
                "OnSettingsViewModelPropertyChanged",
                settingsViewModel,
                new System.ComponentModel.PropertyChangedEventArgs(
                    nameof(SettingsViewModel.AlwaysOnTop))));

            Assert.Null(exception);
            Assert.Equal("Settings save failed", viewModel.StatusText);
        });
    }

    [Fact]
    public void SuccessfulSettingsSaveDoesNotSetUiError()
    {
        RunOnSta(() =>
        {
            var viewModel = new MainViewModel(new MiningSessionCounter());
            var mainWindow = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            using var service = new TrayIconService(
                mainWindow,
                viewModel,
                Dispatcher.CurrentDispatcher,
                (_, _, _) => new RecordingTrayIconResource(),
                () => (Icon)SystemIcons.Application.Clone(),
                settingsSaved: _ => true);
            var settingsViewModel = new SettingsViewModel(new AppSettings());

            InvokePrivateMethod(
                service,
                "OnSettingsViewModelPropertyChanged",
                settingsViewModel,
                new System.ComponentModel.PropertyChangedEventArgs(
                    nameof(SettingsViewModel.AlwaysOnTop)));

            Assert.Equal("Monitoring unavailable", viewModel.StatusText);
        });
    }

    [Fact]
    public void LocationSettingsSaveFailureSetsExistingUiStatusWithoutThrowing()
    {
        RunOnSta(() =>
        {
            var viewModel = new MainViewModel(new MiningSessionCounter());
            var mainWindow = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            using var service = new TrayIconService(
                mainWindow,
                viewModel,
                Dispatcher.CurrentDispatcher,
                (_, _, _) => new RecordingTrayIconResource(),
                () => (Icon)SystemIcons.Application.Clone(),
                settingsSaved: _ => false);
            var settingsViewModel = new SettingsViewModel(new AppSettings());

            var exception = Record.Exception(() => InvokePrivateMethod(
                service,
                "OnSettingsViewModelPropertyChanged",
                settingsViewModel,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(SettingsViewModel.LocationSettingsChanged))));

            Assert.Null(exception);
            Assert.Equal("Settings save failed", viewModel.StatusText);
        });
    }

    [Fact]
    public void SuccessfulLocationSettingsSaveResetsTheViewModelDirtyMarker()
    {
        RunOnSta(() =>
        {
            var viewModel = new MainViewModel(new MiningSessionCounter());
            var mainWindow = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            using var service = new TrayIconService(
                mainWindow,
                viewModel,
                Dispatcher.CurrentDispatcher,
                (_, _, _) => new RecordingTrayIconResource(),
                () => (Icon)SystemIcons.Application.Clone(),
                settingsSaved: _ => true);
            var settingsViewModel = new SettingsViewModel(new AppSettings());
            Assert.True(settingsViewModel.AddAdditionalWurmDataLocation(@"D:\\WurmData"));
            Assert.True(settingsViewModel.CreateSettings().LocationSettingsChanged);

            InvokePrivateMethod(
                service,
                "OnSettingsViewModelPropertyChanged",
                settingsViewModel,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(SettingsViewModel.LocationSettingsChanged)));

            Assert.False(settingsViewModel.CreateSettings().LocationSettingsChanged);
        });
    }

    [Fact]
    public void InitializeCreatesExpectedMenuAndMakesTrayIconVisible()
    {
        Forms.ContextMenuStrip? capturedMenu = null;
        var trayIconResource = new RecordingTrayIconResource();
        var alertsRequested = 0;
        using var service = CreateService(
            (_, menu, _) =>
            {
                capturedMenu = menu;
                return trayIconResource;
            },
            () => alertsRequested++,
            () => null!);

        service.Initialize();

        Assert.True(trayIconResource.Visible);
        var menu = Assert.IsType<Forms.ContextMenuStrip>(capturedMenu);
        Assert.Equal(
            ["Always On Top", "Alerty", "Analyse", "Statistics", "Settings", "Exit"],
            menu.Items.Cast<Forms.ToolStripItem>().Select(item => item.Text));
        GetMenuItem(menu, "Alerty").PerformClick();
        Assert.Equal(1, alertsRequested);
    }

    [Fact]
    public void AnalyseMenuOpensEmptyAndReusesWindowWhileHiddenAndAfterReset()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var vm = new MainViewModel(new MiningSessionCounter(), postToUi: action => Dispatcher.CurrentDispatcher.BeginInvoke(action), analyseProcessor: processor);
            var main = new MainWindow(() => { }) { DataContext = vm };
            Forms.ContextMenuStrip? menu = null;
            using var service = new TrayIconService(main, vm, Dispatcher.CurrentDispatcher,
                (_, captured, _) => { menu = captured; return new RecordingTrayIconResource(); },
                () => (Icon)SystemIcons.Application.Clone());
            try
            {
                service.Initialize();
                var item = GetMenuItem(menu!, "Analyse");
                Assert.True(item.Available && item.Enabled);
                item.PerformClick(); PumpDispatcher();
                var window = Assert.Single(main.OwnedWindows.OfType<AnalyseResultsWindow>());
                Assert.True(window.IsVisible);
                Assert.False(main.IsVisible);
                Assert.Equal(Visibility.Visible, ((System.Windows.Controls.TextBlock)window.FindName("EmptyText")).Visibility);
                Assert.Empty(((System.Windows.Controls.ItemsControl)window.FindName("VeinsList")).Items);
                Assert.Equal("Last analysis: time unavailable", ((System.Windows.Controls.TextBlock)window.FindName("FinishTimeText")).Text);
                item.PerformClick(); PumpDispatcher();
                Assert.Same(window, Assert.Single(main.OwnedWindows.OfType<AnalyseResultsWindow>()));

                var id = PlayerProfileId.CreateNew();
                foreach (var text in new[] { "You start to analyse the shard.", "You see a slight trace of salt.", "[08:27:05] You finish analysing the shard." })
                    processor.Process(new OrderedEventLine(id, "Alice", 1, new EventLogLine(id, "Alice", "event.txt", text)));
                PumpDispatcher();
                Assert.Equal("Shard", ((System.Windows.Controls.TextBlock)window.FindName("InputText")).Text);
                Assert.Single(((System.Windows.Controls.ItemsControl)window.FindName("VeinsList")).Items);
                item.PerformClick(); PumpDispatcher();
                Assert.Same(window, Assert.Single(main.OwnedWindows.OfType<AnalyseResultsWindow>()));
                window.Close();
                main.Show(); main.WindowState = WindowState.Minimized;
                item.PerformClick(); PumpDispatcher();
                window = Assert.Single(main.OwnedWindows.OfType<AnalyseResultsWindow>());
                Assert.True(window.IsVisible);
                Assert.Equal("Shard", ((System.Windows.Controls.TextBlock)window.FindName("InputText")).Text);
                processor.ResetProfile(id); PumpDispatcher();
                Assert.Equal(Visibility.Visible, ((System.Windows.Controls.TextBlock)window.FindName("EmptyText")).Visibility);
                Assert.True(item.Enabled);
                item.PerformClick(); PumpDispatcher();
                Assert.Same(window, Assert.Single(main.OwnedWindows.OfType<AnalyseResultsWindow>()));
            }
            finally { main.Close(); vm.DetachAnalyseUpdates(); }
        });
    }

    [Fact]
    public void TrayInitializationFailureLeavesServiceDisposableAndRetryable()
    {
        var attempts = 0;
        var trayIconResource = new RecordingTrayIconResource();
        using var service = CreateService((_, _, _) =>
        {
            attempts++;
            if (attempts == 1)
                throw new InvalidOperationException("Simulated tray initialization failure.");

            return trayIconResource;
        });

        Assert.Throws<InvalidOperationException>(() => service.Initialize());
        service.Initialize();

        Assert.Equal(2, attempts);
        Assert.True(trayIconResource.Visible);
    }

    [Fact]
    public void DisposeIsIdempotentAndMakesTrayIconInvisible()
    {
        var trayIconResource = new RecordingTrayIconResource();
        using var service = CreateService((_, _, _) => trayIconResource);
        service.Initialize();

        service.Dispose();
        service.Dispose();

        Assert.False(trayIconResource.Visible);
        Assert.Equal(1, trayIconResource.DisposeCalls);
    }

    [Fact]
    public void AlertsWindowIsReactivatedAndClosedWithTrayService()
    {
        RunOnSta(() =>
        {
            Forms.ContextMenuStrip? menu = null;
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
            var controller = new EventAlertConfigurationController(
                settings, new EventAlertCatalog(settings.EventAlerts), _ => true);
            var created = 0;
            AlertsWindow? window = null;
            using var service = CreateService(
                (_, capturedMenu, _) =>
                {
                    menu = capturedMenu;
                    return new RecordingTrayIconResource();
                },
                alertsWindowFactory: () =>
                {
                    created++;
                    return window = new AlertsWindow(controller);
                });

            service.Initialize();
            GetMenuItem(menu!, "Alerty").PerformClick();
            Assert.True(window!.IsVisible);
            GetMenuItem(menu!, "Alerty").PerformClick();
            Assert.Equal(1, created);

            service.Dispose();
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void MainWindowCloseBehaviorDependsOnTrayAvailabilityOnStaThread()
    {
        RunOnSta(() =>
        {
            var trayAvailableWindowShutdownCalls = 0;
            var trayAvailableWindow = new MainWindow(() => trayAvailableWindowShutdownCalls++);
            var trayAvailableWindowClosed = false;
            trayAvailableWindow.Closed += (_, _) => trayAvailableWindowClosed = true;
            trayAvailableWindow.SetTrayAvailable(true);
            trayAvailableWindow.Show();

            trayAvailableWindow.Close();

            Assert.False(trayAvailableWindowClosed);
            Assert.False(trayAvailableWindow.IsVisible);
            Assert.Equal(0, trayAvailableWindowShutdownCalls);

            trayAvailableWindow.RequestExit();
            trayAvailableWindow.Close();

            var trayUnavailableWindowShutdownCalls = 0;
            var trayUnavailableWindow = new MainWindow(() => trayUnavailableWindowShutdownCalls++);
            var trayUnavailableWindowClosed = false;
            trayUnavailableWindow.Closed += (_, _) => trayUnavailableWindowClosed = true;
            trayUnavailableWindow.SetTrayAvailable(false);
            trayUnavailableWindow.Show();

            trayUnavailableWindow.Close();

            Assert.True(trayUnavailableWindowClosed);
            Assert.Equal(1, trayUnavailableWindowShutdownCalls);
        });
    }

    [Fact]
    public void TrayCallbacksHandleSettingsResetTopmostHideAndExitOnStaThread()
    {
        RunOnSta(() =>
        {
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            TrayIconService? service = null;
            MainWindow? mainWindow = null;
            SettingsWindow? latestSettingsWindow = null;
            SettingsViewModel? latestSettingsViewModel = null;
            try
            {
                var counter = new MiningSessionCounter();
                counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
                var resetCallbackStarted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseResetCallback = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var viewModel = new MainViewModel(
                    counter,
                    async () =>
                    {
                        resetCallbackStarted.TrySetResult(true);
                        await releaseResetCallback.Task;
                        counter.Reset();
                    });
                var mainWindowShutdownCalls = 0;
                mainWindow = new MainWindow(() => mainWindowShutdownCalls++) { DataContext = viewModel };

                Forms.ContextMenuStrip? menu = null;
                Action<Forms.MouseEventArgs>? trayMouseClick = null;
                var trayIconResource = new RecordingTrayIconResource();
                var settingsWindowCreations = 0;
                var shutdownCalls = 0;
                var savedSettings = new List<AppSettings>();
                var currentSettings = new AppSettings();
                var openedSettings = new List<AppSettings>();
                var trayAlwaysOnTopValues = new List<bool>();
                var settingsWorkingAreaProvider = new FakeSettingsWindowWorkingAreaProvider(
                    new SettingsWindowWorkingArea(100, 200, 800, 600));
                var resetConfirmed = false;
                service = new TrayIconService(
                    mainWindow,
                    viewModel,
                    Dispatcher.CurrentDispatcher,
                    (_, capturedMenu, onMouseClick) =>
                    {
                        menu = capturedMenu;
                        trayMouseClick = onMouseClick;
                        return trayIconResource;
                    },
                    () => (Icon)SystemIcons.Application.Clone(),
                    () =>
                    {
                        var settingsViewModel = new SettingsViewModel(currentSettings);
                        openedSettings.Add(settingsViewModel.CreateSettings());
                        return settingsViewModel;
                    },
                    settingsViewModel =>
                    {
                        settingsWindowCreations++;
                        latestSettingsViewModel = settingsViewModel;
                        latestSettingsWindow = new SettingsWindow(settingsViewModel, () => resetConfirmed);
                        return latestSettingsWindow;
                    },
                    settings =>
                    {
                        savedSettings.Add(settings);
                        currentSettings = settings;
                        mainWindow.SetAlwaysOnTop(settings.AlwaysOnTop);
                        return true;
                    },
                    enabled =>
                    {
                        trayAlwaysOnTopValues.Add(enabled);
                        currentSettings.AlwaysOnTop = enabled;
                        return true;
                    },
                    () => shutdownCalls++,
                    settingsWorkingAreaProvider);
                mainWindow.SetTrayAvailable(true);
                service.Initialize();
                mainWindow.Show();

                trayMouseClick!(new Forms.MouseEventArgs(Forms.MouseButtons.Right, 1, 0, 0, 0));
                Assert.True(mainWindow.IsVisible);

                trayMouseClick(new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0));
                Assert.False(mainWindow.IsVisible);

                trayMouseClick(new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0));
                Assert.True(mainWindow.IsVisible);

                mainWindow.WindowState = WindowState.Minimized;
                trayMouseClick(new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0));
                Assert.True(mainWindow.IsVisible);
                Assert.Equal(WindowState.Normal, mainWindow.WindowState);

                trayMouseClick(new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0));
                Assert.False(mainWindow.IsVisible);
                trayMouseClick(new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0));
                Assert.True(mainWindow.IsVisible);

                var settingsItem = GetMenuItem(menu!, "Settings");
                var alwaysOnTopItem = GetMenuItem(menu!, "Always On Top");
                settingsItem.PerformClick();
                Assert.Equal(1, settingsWindowCreations);
                Assert.Empty(savedSettings);
                var currentSettingsWindow = Assert.IsType<SettingsWindow>(latestSettingsWindow);
                Assert.Null(currentSettingsWindow.Owner);
                AssertBottomRightPlacement(currentSettingsWindow, settingsWorkingAreaProvider.WorkingArea);
                var informationalVersion = typeof(SettingsWindow).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;
                var expectedDisplayVersion = informationalVersion?
                    .Split('+', 2)[0];
                Assert.False(string.IsNullOrWhiteSpace(currentSettingsWindow.VersionText));
                Assert.StartsWith("v", currentSettingsWindow.VersionText);
                Assert.DoesNotContain("+", currentSettingsWindow.VersionText);
                Assert.Equal($"v{expectedDisplayVersion}", currentSettingsWindow.VersionText);

                InvokePrivateMethod(
                    currentSettingsWindow,
                    "OnOpenDiagnosticsWindowClick",
                    currentSettingsWindow,
                    new RoutedEventArgs());
                var diagnosticsWindow = Assert.IsType<DiagnosticsWindow>(typeof(TrayIconService)
                    .GetField("_diagnosticsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(service));
                Assert.Null(diagnosticsWindow.Owner);

                settingsWorkingAreaProvider.WorkingArea = new SettingsWindowWorkingArea(1200, 80, 640, 480);
                settingsItem.PerformClick();
                Assert.Equal(1, settingsWindowCreations);
                AssertBottomRightPlacement(currentSettingsWindow, settingsWorkingAreaProvider.WorkingArea);

                latestSettingsViewModel!.AlwaysOnTop = true;
                var alwaysOnTopSaved = Assert.Single(savedSettings);
                Assert.True(alwaysOnTopSaved.AlwaysOnTop);
                Assert.True(mainWindow.Topmost);
                Assert.True(mainWindow.IsVisible);

                latestSettingsViewModel.StartMinimizedToTray = true;
                Assert.Equal(2, savedSettings.Count);
                Assert.True(savedSettings[1].StartMinimizedToTray);
                Assert.True(mainWindow.IsVisible);

                latestSettingsViewModel.StartMinimizedToTray = true;
                Assert.Equal(2, savedSettings.Count);

                latestSettingsWindow!.RequestResetSession();
                Assert.NotEmpty(counter.GetSnapshot());
                resetConfirmed = true;
                latestSettingsWindow.RequestResetSession();

                Assert.True(resetCallbackStarted.Task.IsCompleted);
                Assert.NotEmpty(counter.GetSnapshot());
                Assert.NotEmpty(viewModel.Profiles);

                releaseResetCallback.TrySetResult(true);
                PumpDispatcherUntil(() => viewModel.Profiles.Count == 0);

                Assert.Empty(counter.GetSnapshot());
                Assert.Empty(viewModel.Profiles);

                latestSettingsWindow.Close();
                latestSettingsViewModel.AlwaysOnTop = false;
                Assert.Equal(2, savedSettings.Count);

                settingsItem.PerformClick();
                Assert.Equal(2, settingsWindowCreations);
                Assert.Equal(2, openedSettings.Count);
                Assert.True(openedSettings[1].AlwaysOnTop);
                Assert.True(openedSettings[1].StartMinimizedToTray);

                alwaysOnTopItem.PerformClick();
                Assert.False(mainWindow.Topmost);
                Assert.False(alwaysOnTopItem.Checked);
                Assert.Equal([false], trayAlwaysOnTopValues);

                latestSettingsWindow!.Close();
                settingsItem.PerformClick();
                Assert.Equal(3, settingsWindowCreations);
                Assert.False(openedSettings[2].AlwaysOnTop);

                var closed = false;
                mainWindow.Closed += (_, _) => closed = true;
                mainWindow.Close();
                Assert.False(closed);
                Assert.False(mainWindow.IsVisible);

                mainWindow.ShowAndActivate();
                var exitItem = GetMenuItem(menu!, "Exit");
                exitItem.PerformClick();
                Assert.Equal(1, shutdownCalls);
                Assert.Equal(0, mainWindowShutdownCalls);
                Assert.False(trayIconResource.Visible);
                Assert.Equal(1, trayIconResource.DisposeCalls);

                mainWindow.Close();
                Assert.True(closed);
                Assert.Equal(0, mainWindowShutdownCalls);
                service.Dispose();
                Assert.Equal(1, trayIconResource.DisposeCalls);
            }
            finally
            {
                latestSettingsWindow?.Close();
                if (mainWindow is { IsVisible: true })
                {
                    mainWindow.RequestExit();
                    mainWindow.Close();
                }

                service?.Dispose();
                application.Shutdown();
            }
        });
    }

    [Fact]
    public void ResetFailureDoesNotReachDispatcherUnhandledException()
    {
        RunOnSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var viewModel = new MainViewModel(
                new MiningSessionCounter(),
                () => Task.FromException(new InvalidOperationException("Simulated reset failure.")));
            using var service = CreateService(viewModel);
            Exception? unhandledException = null;
            DispatcherUnhandledExceptionEventHandler handler = (_, eventArgs) =>
            {
                unhandledException = eventArgs.Exception;
                eventArgs.Handled = true;
            };
            Dispatcher.CurrentDispatcher.UnhandledException += handler;

            try
            {
                InvokePrivateMethod(service, "OnResetSessionConfirmed", null, EventArgs.Empty);
                PumpDispatcher();

                Assert.Null(unhandledException);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.UnhandledException -= handler;
            }
        });
    }

    [Fact]
    public void DispatcherActionFailureDoesNotPropagate()
    {
        RunOnSta(() =>
        {
            using var service = CreateService(new MainViewModel(new MiningSessionCounter()));
            var actionRan = false;

            var exception = Record.Exception(() => InvokePrivateMethod(
                service,
                "RunOnDispatcher",
                (Action)(() =>
                {
                    actionRan = true;
                    throw new InvalidOperationException("Simulated tray action failure.");
                })));

            Assert.True(actionRan);
            Assert.Null(exception);
        });
    }

    [Fact]
    public void StatisticsUsesSkillStatisticsWindowWithSingletonAndCloseLifecycle()
    {
        RunOnSta(() =>
        {
            Forms.ContextMenuStrip? menu = null;
            using var monitor = new LiveSkillMonitor(Path.GetTempPath(), TimeSpan.FromDays(1));
            var created = new List<SkillStatisticsWindow>();
            using var service = CreateService(
                (_, capturedMenu, _) =>
                {
                    menu = capturedMenu;
                    return new RecordingTrayIconResource();
                },
                skillStatisticsWindowFactory: () =>
                {
                    var window = new SkillStatisticsWindow(
                        new SkillStatisticsViewModel(monitor, action => action()));
                    created.Add(window);
                    return window;
                });

            service.Initialize();
            var statistics = GetMenuItem(menu!, "Statistics");
            statistics.PerformClick();
            var first = Assert.Single(created);
            Assert.True(first.IsVisible);

            statistics.PerformClick();
            Assert.Single(created);

            first.Close();
            statistics.PerformClick();
            Assert.Equal(2, created.Count);
            var second = created[1];
            Assert.True(second.IsVisible);

            service.Dispose();
            Assert.False(second.IsVisible);
        });
    }

    [Fact]
    public void StatisticsReopen_RestoresLastSelectedProfileWhenStillAvailable()
    {
        RunOnSta(() =>
        {
            Forms.ContextMenuStrip? menu = null;
            using var fixture = new StatisticsMonitorFixture("Alice", "Bob");
            var created = new List<SkillStatisticsWindow>();
            using var service = CreateService(
                (_, capturedMenu, _) =>
                {
                    menu = capturedMenu;
                    return new RecordingTrayIconResource();
                },
                skillStatisticsWindowFactory: () =>
                {
                    var window = new SkillStatisticsWindow(
                        new SkillStatisticsViewModel(fixture.Monitor, action => action()));
                    created.Add(window);
                    return window;
                });

            service.Initialize();
            var statistics = GetMenuItem(menu!, "Statistics");
            statistics.PerformClick();
            var firstViewModel = Assert.IsType<SkillStatisticsViewModel>(Assert.Single(created).DataContext);
            Assert.Equal("Alice", firstViewModel.Profiles.Single(profile =>
                profile.ProfileId == firstViewModel.SelectedProfileId).ProfileName);
            firstViewModel.SelectedProfileId = firstViewModel.Profiles.Single(profile =>
                profile.ProfileName == "Bob").ProfileId;
            created[0].Close();

            statistics.PerformClick();

            var secondViewModel = Assert.IsType<SkillStatisticsViewModel>(created[1].DataContext);
            Assert.Equal("Bob", secondViewModel.Profiles.Single(profile =>
                profile.ProfileId == secondViewModel.SelectedProfileId).ProfileName);
        });
    }

    [Fact]
    public void StatisticsReopen_FallsBackWhenLastSelectedProfileIsUnavailable()
    {
        RunOnSta(() =>
        {
            Forms.ContextMenuStrip? menu = null;
            using var firstFixture = new StatisticsMonitorFixture("Alice", "Bob");
            using var secondFixture = new StatisticsMonitorFixture("Alice");
            var created = new List<SkillStatisticsWindow>();
            var factoryCalls = 0;
            using var service = CreateService(
                (_, capturedMenu, _) =>
                {
                    menu = capturedMenu;
                    return new RecordingTrayIconResource();
                },
                skillStatisticsWindowFactory: () =>
                {
                    var monitor = factoryCalls++ == 0 ? firstFixture.Monitor : secondFixture.Monitor;
                    var window = new SkillStatisticsWindow(
                        new SkillStatisticsViewModel(monitor, action => action()));
                    created.Add(window);
                    return window;
                });

            service.Initialize();
            var statistics = GetMenuItem(menu!, "Statistics");
            statistics.PerformClick();
            var firstViewModel = Assert.IsType<SkillStatisticsViewModel>(Assert.Single(created).DataContext);
            firstViewModel.SelectedProfileId = firstViewModel.Profiles.Single(profile =>
                profile.ProfileName == "Bob").ProfileId;
            created[0].Close();

            statistics.PerformClick();

            var secondViewModel = Assert.IsType<SkillStatisticsViewModel>(created[1].DataContext);
            Assert.Equal(["Alice"], secondViewModel.Profiles.Select(profile => profile.ProfileName));
            Assert.Equal("Alice", secondViewModel.Profiles.Single(profile =>
                profile.ProfileId == secondViewModel.SelectedProfileId).ProfileName);
        });
    }

    private static TrayIconService CreateService(
        Func<Icon, Forms.ContextMenuStrip, Action<Forms.MouseEventArgs>, ITrayIconResource> trayIconResourceFactory,
        Action? alertsRequested = null,
        Func<SkillStatisticsWindow>? skillStatisticsWindowFactory = null,
        Func<AlertsWindow>? alertsWindowFactory = null)
    {
        var mainWindow = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        var viewModel = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        return new TrayIconService(
            mainWindow,
            viewModel,
            Dispatcher.CurrentDispatcher,
            trayIconResourceFactory,
            () => (Icon)SystemIcons.Application.Clone(),
            skillStatisticsWindowFactory: skillStatisticsWindowFactory,
            alertsRequested: alertsRequested,
            alertsWindowFactory: alertsWindowFactory);
    }

    private static TrayIconService CreateService(MainViewModel viewModel)
    {
        var mainWindow = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        return new TrayIconService(
            mainWindow,
            viewModel,
            Dispatcher.CurrentDispatcher,
            (_, _, _) => new RecordingTrayIconResource(),
            () => (Icon)SystemIcons.Application.Clone());
    }

    private static void InvokePrivateMethod(object instance, string methodName, params object?[] arguments)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{methodName} was not found.");
        method.Invoke(instance, arguments);
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            (Action)(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static Forms.ToolStripMenuItem GetMenuItem(Forms.ContextMenuStrip menu, string text)
    {
        return menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == text);
    }

    private static void AssertBottomRightPlacement(
        SettingsWindow settingsWindow,
        SettingsWindowWorkingArea workingArea)
    {
        Assert.Equal(
            workingArea.Left + workingArea.Width - settingsWindow.Width - 12,
            settingsWindow.Left,
            precision: 6);
        Assert.Equal(
            workingArea.Top + workingArea.Height - settingsWindow.Height - 12,
            settingsWindow.Top,
            precision: 6);
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caughtException)
            {
                exception = caughtException;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The STA tray test did not complete within the timeout.");
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private static void PumpDispatcherUntil(Func<bool> condition)
    {
        if (condition())
            return;

        var frame = new DispatcherFrame();
        void Continue()
        {
            if (condition())
            {
                frame.Continue = false;
                return;
            }

            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                (Action)Continue);
        }

        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            (Action)Continue);
        Dispatcher.PushFrame(frame);
    }

    private sealed class RecordingTrayIconResource : ITrayIconResource
    {
        public bool Visible { get; set; }

        public int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
        }
    }

    private sealed class StatisticsMonitorFixture : IDisposable
    {
        public StatisticsMonitorFixture(params string[] profiles)
        {
            Root = Path.Combine(Path.GetTempPath(), $"WurmMiningCounter.Tests-{Guid.NewGuid():N}");
            var playersRoot = Path.Combine(Root, "players");
            Directory.CreateDirectory(playersRoot);
            Monitor = new LiveSkillMonitor(
                playersRoot,
                TimeSpan.FromDays(1),
                profileIdentityResolver: new PlayerProfileIdentityResolver(
                    Path.Combine(Root, "identity-registry")));
            foreach (var profile in profiles)
            {
                var sourcePath = Path.Combine(playersRoot, profile, "logs", "_Skills.2026-10.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                File.WriteAllText(sourcePath, "[10:00:00] Mining increased by 0,001000 to 80,000000\n");
                File.WriteAllText(Path.Combine(Root, $"console.{profile}.log"), $"[10:00:00] Writing to {sourcePath}\n");
            }

            Monitor.Start();
        }

        public string Root { get; }

        public LiveSkillMonitor Monitor { get; }

        public void Dispose()
        {
            Monitor.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeSettingsWindowWorkingAreaProvider(SettingsWindowWorkingArea workingArea)
        : ISettingsWindowWorkingAreaProvider
    {
        public SettingsWindowWorkingArea WorkingArea { get; set; } = workingArea;

        public SettingsWindowWorkingArea GetWorkingArea(MainWindow mainWindow)
        {
            ArgumentNullException.ThrowIfNull(mainWindow);
            return WorkingArea;
        }
    }
}
