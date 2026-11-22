using System.Windows;
using WurmMiningCounter.Models;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;
using WurmMiningCounter.Views;

namespace WurmMiningCounter
{
    internal sealed class MonitoringHealthState
    {
        private readonly object _sync = new();
        private readonly MainViewModel _viewModel;
        private readonly Action<Action> _postToUi;
        private IReadOnlyList<PlayerProfileIdentityActivationStatus> _identityStatuses = [];
        private bool _terminalFault;
        private bool _closed;
        private long _revision;

        internal MonitoringHealthState(MainViewModel viewModel, Action<Action> postToUi)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        }

        internal void UpdateIdentity(IReadOnlyList<PlayerProfileIdentityActivationStatus> statuses)
        {
            ArgumentNullException.ThrowIfNull(statuses);
            Publish(statuses.ToArray(), terminalFault: null);
        }

        internal void RecordTerminalFault() => Publish(null, terminalFault: true);

        internal void Close()
        {
            lock (_sync)
            {
                _closed = true;
                _revision++;
            }
        }

        private void Publish(IReadOnlyList<PlayerProfileIdentityActivationStatus>? statuses, bool? terminalFault)
        {
            string status;
            long revision;
            lock (_sync)
            {
                if (_closed)
                    return;
                if (statuses is not null)
                    _identityStatuses = statuses;
                if (terminalFault is true)
                    _terminalFault = true;
                status = GetStatus();
                revision = ++_revision;
            }

            try
            {
                _postToUi(() =>
                {
                    lock (_sync)
                    {
                        if (_closed || revision != _revision)
                            return;
                        _viewModel.SetStatus(status);
                    }
                });
            }
            catch
            {
                // Health reporting must not affect source or module handling.
            }
        }

        private string GetStatus()
        {
            if (_terminalFault)
                return "Monitoring error";
            if (_identityStatuses.Any(status => status.IsRestartRequired))
                return "Monitoring - Event owner changed; restart required";
            if (_identityStatuses.Any(status => status.IsOwnershipConflict))
                return "Monitoring - ownership conflict; Event paused";
            if (!_identityStatuses.Any(status => status.HasIssue))
                return "Monitoring";
            return _identityStatuses.Any(status => status.IsActive)
                ? "Monitoring identity warning"
                : "Monitoring identity error";
        }
    }

    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private MiningSessionCounter? _counter;
        private MiningInspirationCounter? _miningInspirationCounter;
        private MiningInspirationTracker? _miningInspirationTracker;
        private MiningInspirationBoundaryObserver? _miningInspirationBoundaryObserver;
        private MiningEventLane? _miningEventLane;
        private EventLineCoordinator? _eventLineCoordinator;
        private FastEventDispatcher? _eventDispatcher;
        private EventPipeline? _eventPipeline;
        private VeinContextProcessor? _veinContextProcessor;
        private NearbyScanProcessor? _nearbyScanProcessor;
        private AnalyseProcessor? _analyseProcessor;
        private EventAlertCatalog? _eventAlertCatalog;
        private EventAlertModule? _eventAlertModule;
        private EventAlertDelivery? _eventAlertDelivery;
        private EventAlertPopupHost? _eventAlertPopupHost;
        private EventAlertConfigurationController? _eventAlertConfiguration;
        private TrayIconService? _trayIconService;
        private SingleInstanceService? _singleInstanceService;
        private MainViewModel? _mainViewModel;
        private LiveSkillMonitor? _liveSkillMonitor;
        private MiningStatisticsSessionBaselineStore? _miningStatisticsSessionBaselines;
        private PlayerProfileIdentityResolver? _playerProfileIdentityResolver;
        private LogDiscoverySnapshotService? _logDiscoverySnapshots;
        private readonly object _snapshotReconciliationSync = new();
        private readonly Dictionary<string, SnapshotRetirementState> _snapshotRetirements = new(StringComparer.Ordinal);
        private CancellationTokenSource? _snapshotReconciliationCancellation;
        private Task _snapshotReconciliationTail = Task.CompletedTask;
        private long _lastQueuedSnapshotVersion;
        private bool _snapshotReconciliationSubscribed;
        private MonitoringHealthState? _monitoringHealth;
        private SettingsService _settingsService = null!;
        private AppSettings _settings = null!;
        private readonly RuntimeDiagnosticService _diagnostics = new();
        private int _shutdownPrepared;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _diagnostics.Report(RuntimeDiagnosticLevel.Info, nameof(App), "Application starting.");

            try
            {
                _singleInstanceService = new SingleInstanceService(ActivatePrimaryInstance);
            }
            catch (Exception exception)
            {
                _diagnostics.Report(RuntimeDiagnosticLevel.Error, nameof(App), "Application startup failed while initializing SingleInstanceService: {0}", exception);
                RequestShutdown();
                return;
            }

            if (!_singleInstanceService.IsPrimary)
            {
                _singleInstanceService.SendActivate();
                RequestShutdown();
                return;
            }

            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

            StartApplicationAsync();
        }

        private async void StartApplicationAsync()
        {
            try
            {
            _settingsService = new SettingsService(_diagnostics);
            _settings = _settingsService.Load();
            _diagnostics.Enabled = _settings.DiagnosticsEnabled;

            _counter = new MiningSessionCounter();
            _playerProfileIdentityResolver = new PlayerProfileIdentityResolver();
            _logDiscoverySnapshots = new LogDiscoverySnapshotService(_playerProfileIdentityResolver);
            var startupSnapshot = await Task.Run(() => _logDiscoverySnapshots.Discover(
                _settings.KnownLogLocations, _settings.AdditionalWurmDataLocations));
            if (!startupSnapshot.IsComplete)
            {
                _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(App),
                    "Startup log discovery completed with partial results.");
            }
            _miningStatisticsSessionBaselines = new MiningStatisticsSessionBaselineStore();
            _miningInspirationCounter = new MiningInspirationCounter();
            _miningInspirationTracker = new MiningInspirationTracker(_miningInspirationCounter);
            var parser = new MiningEventParser();
            var processor = new MiningEventProcessor(parser, _counter, _miningInspirationTracker);
            _eventLineCoordinator = new EventLineCoordinator();
            _nearbyScanProcessor = new NearbyScanProcessor(_diagnostics);
            _analyseProcessor = new AnalyseProcessor();
            _veinContextProcessor = new VeinContextProcessor(
                _diagnostics,
                profileId => _nearbyScanProcessor.ResetProfile(profileId));
            _eventLineCoordinator.RegisterConsumer(_veinContextProcessor);
            _eventLineCoordinator.RegisterConsumer(_nearbyScanProcessor);
            _eventLineCoordinator.RegisterConsumer(_analyseProcessor);
            _eventLineCoordinator.ProfileLaneDrained = _analyseProcessor.ReleaseProfile;
            _eventAlertCatalog = new EventAlertCatalog(_settings.EventAlerts);
            _eventAlertConfiguration = new EventAlertConfigurationController(
                _settings, _eventAlertCatalog, _settingsService.Save);
            _eventAlertPopupHost = new EventAlertPopupHost(Dispatcher);
            (_eventAlertModule, _eventAlertDelivery) = WireEventAlerts(
                _eventLineCoordinator,
                _eventAlertCatalog,
                new ExternalSoundService(),
                _eventAlertPopupHost.Enqueue);
            _miningInspirationBoundaryObserver = new MiningInspirationBoundaryObserver(_miningInspirationTracker!);

            var sourceCandidates = startupSnapshot.SourceCandidates;
            var sourceRegistrationAnchor = sourceCandidates.FirstOrDefault()?.ProfileDirectoryPath ?? AppContext.BaseDirectory;
            _liveSkillMonitor = new LiveSkillMonitor(
                sourceRegistrationAnchor,
                LiveSkillMonitor.DefaultPollingInterval,
                _diagnostics,
                startWorker: true,
                profileIdentityResolver: _playerProfileIdentityResolver,
                sourceCandidates: sourceCandidates);
            _miningEventLane = new MiningEventLane(
                processor,
                _miningInspirationBoundaryObserver,
                CaptureMiningTickResetBoundary,
                ReportMiningTickResetBoundaryFailure);
            _eventDispatcher = new FastEventDispatcher(_miningEventLane, _eventLineCoordinator);
            _eventPipeline = new EventPipeline(
                sourceRegistrationAnchor,
                _eventDispatcher,
                _playerProfileIdentityResolver,
                sourceCandidates);
            var startupPublication = _logDiscoverySnapshots.GetCurrentSnapshot();
            if (startupPublication is not null)
                _eventPipeline.PublishDiscoverySnapshot(startupPublication.Version, startupPublication.Snapshot.SourceCandidates,
                    startupPublication.Snapshot.Discovery.SourceOwnershipAmbiguities,
                    startupPublication.Snapshot.IsComplete && startupPublication.Snapshot.Discovery.Uncertainties.Count == 0
                    && startupPublication.Snapshot.Discovery.Rejections.Count == 0);
            WireEventPipelineDiagnostics(_eventPipeline, _eventDispatcher);

            var viewModel = new MainViewModel(
                _counter,
                resetSessionAsync: _eventDispatcher is null ? null : ResetEventSessionAsync,
                initialVeinContexts: _veinContextProcessor.GetActiveContextSnapshots(),
                veinContextProcessor: _veinContextProcessor,
                postToUi: action => _ = Dispatcher.BeginInvoke(action),
                resetVeinContextAsync: _eventDispatcher is null ? null : profileId =>
                    _eventLineCoordinator.ResetProfileAsync(profileId),
                resetMiningInspirationAsync: _eventDispatcher is null
                    ? null
                    : profileId => _eventDispatcher!.ResetMiningInspirationAsync(profileId),
                clearMiningInspirationPending: _miningInspirationTracker.ClearProfilePending,
                miningInspirationCounter: _miningInspirationCounter,
                initialNearbyScans: _nearbyScanProcessor.GetSnapshots(),
                nearbyScanProcessor: _nearbyScanProcessor,
                liveSkillMonitor: _liveSkillMonitor,
                analyseProcessor: _analyseProcessor);
            _mainViewModel = viewModel;
            _monitoringHealth = new MonitoringHealthState(viewModel, action => _ = Dispatcher.BeginInvoke(action));
            if (_eventPipeline is not null && _eventDispatcher is not null)
            {
                WireEventPipelineUiHealth(
                    _eventPipeline,
                    _eventDispatcher,
                    _monitoringHealth);
            }
            if (_eventPipeline is null)
                viewModel.SetStatus("Monitoring unavailable");
            var mainWindow = new MainWindow(
                RequestShutdown,
                SaveWindowPlacement)
            {
                DataContext = viewModel
            };
            mainWindow.SetAlwaysOnTop(_settings.AlwaysOnTop);
            ApplySavedWindowPlacement(mainWindow, _settings.WindowPlacement, new WindowPlacementService());

            MainWindow = mainWindow;

            var isTrayInitialized = false;
            try
            {
                _trayIconService = new TrayIconService(
                    mainWindow,
                    viewModel,
                    () => new SettingsViewModel(_settings, _diagnostics, _logDiscoverySnapshots!,
                        action => _ = Dispatcher.BeginInvoke(action)),
                    settings => ApplySettings(settings, mainWindow),
                    SaveAlwaysOnTopSetting,
                    _diagnostics,
                    _liveSkillMonitor is null
                        ? null
                        : () => new SkillStatisticsWindow(new SkillStatisticsViewModel(
                            _liveSkillMonitor,
                            _counter,
                            action => _ = Dispatcher.BeginInvoke(action),
                            sessionBaselines: _miningStatisticsSessionBaselines)),
                    alertsWindowFactory: () => new AlertsWindow(_eventAlertConfiguration!),
                    shutdown: RequestShutdown);
                _trayIconService.Initialize();
                isTrayInitialized = true;
            }
            catch (Exception exception)
            {
                _diagnostics.Report(RuntimeDiagnosticLevel.Error, nameof(App), "Application tray initialization failed: {0}", exception);
                _trayIconService?.Dispose();
                _trayIconService = null;
            }

            if (isTrayInitialized)
            {
                mainWindow.SetTrayAvailable(true);
                if (!_settings.StartMinimizedToTray)
                    mainWindow.Show();
            }
            else
            {
                mainWindow.SetTrayAvailable(false);
                mainWindow.Show();
            }

            if (_eventPipeline is not null)
            {
                viewModel.SetStatus("Monitoring unavailable");
                try
                {
                    WireIdentityActivationUiHealth(
                        _playerProfileIdentityResolver!,
                        _monitoringHealth,
                        _diagnostics);
                    await Task.Run(() =>
                    {
                        _liveSkillMonitor!.Start();
                        _eventPipeline.Start();
                    });
                    StartSnapshotReconciliation();
                }
                catch (Exception exception)
                {
                    _diagnostics.Report(RuntimeDiagnosticLevel.Error, nameof(App), "Application Event Pipeline startup failed: {0}", exception);
                    viewModel.SetStatus("Monitoring error");
                }
            }
            _diagnostics.Report(RuntimeDiagnosticLevel.Info, nameof(App), "Application started.");
            }
            catch (Exception exception)
            {
                _diagnostics.Report(RuntimeDiagnosticLevel.Error, nameof(App), "Application startup pipeline failed: {0}", exception);
                HandleStartupFailure();
            }
        }

        internal static (EventAlertModule Module, EventAlertDelivery Delivery) WireEventAlerts(
            EventLineCoordinator coordinator,
            EventAlertCatalog catalog,
            ExternalSoundService sounds,
            Action<EventAlertNotification> popupSink)
        {
            ArgumentNullException.ThrowIfNull(coordinator);
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(sounds);
            ArgumentNullException.ThrowIfNull(popupSink);

            var delivery = new EventAlertDelivery(sounds, popupSink);
            var module = new EventAlertModule(catalog, delivery.Deliver);
            coordinator.RegisterConsumer(module);
            return (module, delivery);
        }

        private async Task ResetEventSessionAsync()
        {
            if (_eventDispatcher is null || _counter is null || _veinContextProcessor is null
                || _miningInspirationCounter is null || _miningInspirationTracker is null || _nearbyScanProcessor is null)
            {
                throw new InvalidOperationException("The Event Pipeline is not configured.");
            }

            var profiles = await ResetEventSessionAsync(
                _eventDispatcher,
                _counter,
                _miningInspirationCounter,
                _miningInspirationTracker,
                _veinContextProcessor,
                _nearbyScanProcessor,
                _analyseProcessor).ConfigureAwait(false);
            CaptureSkillOnlyMiningTickResetBoundaries(profiles);
        }

        internal static async Task<IReadOnlyList<PlayerProfileId>> ResetEventSessionAsync(
            FastEventDispatcher dispatcher,
            MiningSessionCounter counter,
            MiningInspirationCounter miningInspirationCounter,
            MiningInspirationTracker miningInspirationTracker,
            VeinContextProcessor veinContextProcessor,
            NearbyScanProcessor nearbyScanProcessor,
            AnalyseProcessor? analyseProcessor = null)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(counter);
            ArgumentNullException.ThrowIfNull(miningInspirationCounter);
            ArgumentNullException.ThrowIfNull(miningInspirationTracker);
            ArgumentNullException.ThrowIfNull(veinContextProcessor);
            ArgumentNullException.ThrowIfNull(nearbyScanProcessor);

            var profiles = GetEventSessionProfiles(
                dispatcher, counter, miningInspirationCounter, veinContextProcessor, nearbyScanProcessor, analyseProcessor);
            await Task.WhenAll(profiles.Select(profileId => dispatcher.ResetProfileOrReleasedAsync(
                profileId,
                () => ResetReleasedEventProfile(
                    profileId, counter, miningInspirationTracker, veinContextProcessor, nearbyScanProcessor, analyseProcessor))))
                .ConfigureAwait(false);
            return profiles;
        }

        private static void ResetReleasedEventProfile(
            PlayerProfileId profileId,
            MiningSessionCounter counter,
            MiningInspirationTracker miningInspirationTracker,
            VeinContextProcessor veinContextProcessor,
            NearbyScanProcessor nearbyScanProcessor,
            AnalyseProcessor? analyseProcessor)
        {
            counter.ResetProfile(profileId);
            miningInspirationTracker.ResetProfile(profileId);
            veinContextProcessor.ResetProfile(profileId);
            nearbyScanProcessor.ResetProfile(profileId);
            analyseProcessor?.ResetProfile(profileId);
        }

        private void CaptureSkillOnlyMiningTickResetBoundaries(IReadOnlyList<PlayerProfileId> eventProfiles)
        {
            if (_liveSkillMonitor is null || _miningStatisticsSessionBaselines is null)
                return;

            CaptureSkillOnlyMiningTickResetBoundaries(
                _miningStatisticsSessionBaselines,
                _liveSkillMonitor.GetSnapshots(),
                eventProfiles,
                TimeProvider.System.GetUtcNow(),
                ReportSkillOnlyMiningTickResetBoundaryFailure);
        }

        private void CaptureMiningTickResetBoundary(PlayerProfileId profileId)
        {
            if (_liveSkillMonitor is not null && _miningStatisticsSessionBaselines is not null)
                CaptureMiningSessionBaseline(
                    _miningStatisticsSessionBaselines,
                    _liveSkillMonitor.GetSnapshots(),
                    profileId,
                    TimeProvider.System.GetUtcNow());
        }

        internal static void CaptureSkillOnlyMiningTickResetBoundaries(
            MiningStatisticsSessionBaselineStore baselines,
            IReadOnlyList<LiveSkillProfileSnapshot> profiles,
            IReadOnlyList<PlayerProfileId> eventProfiles,
            DateTimeOffset sessionStartedAtUtc,
            Action<PlayerProfileId, Exception> reportFailure)
        {
            ArgumentNullException.ThrowIfNull(baselines);
            ArgumentNullException.ThrowIfNull(profiles);
            ArgumentNullException.ThrowIfNull(eventProfiles);
            ArgumentNullException.ThrowIfNull(reportFailure);

            var eventProfileIds = eventProfiles.ToHashSet();
            foreach (var profile in profiles)
            {
                if (!eventProfileIds.Contains(profile.ProfileId))
                {
                    try
                    {
                        CaptureMiningSessionBaseline(baselines, profiles, profile.ProfileId, sessionStartedAtUtc);
                    }
                    catch (Exception exception)
                    {
                        reportFailure(profile.ProfileId, exception);
                    }
                }
            }
        }

        internal static void CaptureMiningSessionBaseline(
            MiningStatisticsSessionBaselineStore baselines,
            IReadOnlyList<LiveSkillProfileSnapshot> profiles,
            PlayerProfileId profileId,
            DateTimeOffset sessionStartedAtUtc)
        {
            ArgumentNullException.ThrowIfNull(baselines);
            ArgumentNullException.ThrowIfNull(profiles);
            var mining = profiles
                .FirstOrDefault(profile => profile.ProfileId == profileId)?
                .Skills.FirstOrDefault(skill => string.Equals(skill.SkillName, "Mining", StringComparison.Ordinal));
            baselines.CaptureMiningSessionBaseline(
                profileId,
                mining?.PositiveUpdateCount ?? 0,
                mining?.SessionGain ?? 0m,
                sessionStartedAtUtc);
        }

        private void ReportMiningTickResetBoundaryFailure(PlayerProfileId profileId, Exception exception) =>
            _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(App),
                "Could not capture the Mining tick reset boundary for profile '{0}': {1}",
                profileId, exception.GetType().Name);

        private void ReportSkillOnlyMiningTickResetBoundaryFailure(PlayerProfileId profileId, Exception exception) =>
            _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(App),
                "Could not capture the Mining tick baseline for skill-only profile '{0}' during session reset: {1}",
                profileId, exception.GetType().Name);

        internal static IReadOnlyList<PlayerProfileId> GetEventSessionProfiles(
            FastEventDispatcher dispatcher,
            MiningSessionCounter counter,
            MiningInspirationCounter miningInspirationCounter,
            VeinContextProcessor veinContextProcessor,
            NearbyScanProcessor nearbyScanProcessor,
            AnalyseProcessor? analyseProcessor = null)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(counter);
            ArgumentNullException.ThrowIfNull(miningInspirationCounter);
            ArgumentNullException.ThrowIfNull(veinContextProcessor);
            ArgumentNullException.ThrowIfNull(nearbyScanProcessor);

            return dispatcher.GetKnownProfiles().Select(profile => profile.Id)
                .Concat(counter.GetProfileSnapshots().Keys)
                .Concat(miningInspirationCounter.GetProfileSnapshots().Keys)
                .Concat(veinContextProcessor.GetActiveContextSnapshots().Select(snapshot => snapshot.ProfileId))
                .Concat(nearbyScanProcessor.GetSnapshots().Select(snapshot => snapshot.ProfileId))
                .Concat(veinContextProcessor.GetPlainRockProspectingResultProfiles())
                .Concat(analyseProcessor?.GetProfiles() ?? [])
                .Distinct()
                .ToArray();
        }


        private void WireEventPipelineDiagnostics(
            EventPipeline pipeline,
            FastEventDispatcher dispatcher)
        {
            pipeline.SourceDiscovered += status => ReportSourceStatus(
                RuntimeDiagnosticLevel.Info, "Source discovered", status);
            pipeline.SourceBaselined += status => ReportSourceStatus(
                RuntimeDiagnosticLevel.Info, "Source baselined", status);
            pipeline.TerminalSourceFaulted += fault => _diagnostics.ReportProfile(
                RuntimeDiagnosticLevel.Error,
                nameof(EventPipeline),
                fault.ProfileName,
                "TerminalSourceFault",
                $"Segment={System.IO.Path.GetFileName(fault.CanonicalPath)} Generation={fault.SourceGeneration} Error={fault.Exception.GetType().Name}");
            dispatcher.TerminalStatusRecorded += status => _diagnostics.ReportProfile(
                RuntimeDiagnosticLevel.Error,
                nameof(FastEventDispatcher),
                status.ProfileName,
                $"Terminal{status.Module}Fault",
                $"Segment={System.IO.Path.GetFileName(status.CanonicalPath)} Generation={status.SourceGeneration} Error={status.Failure.GetType().Name} ReconciliationRequired=true");
            dispatcher.ProfileReleaseStarted += profileName => _diagnostics.ReportProfile(
                RuntimeDiagnosticLevel.Info,
                nameof(FastEventDispatcher),
                profileName,
                "ProfileRelease",
                "Profile lane release started.");
        }

        internal static void WireEventPipelineUiHealth(
            EventPipeline pipeline,
            FastEventDispatcher dispatcher,
            MainViewModel viewModel,
            Action<Action> postToUi)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(viewModel);
            ArgumentNullException.ThrowIfNull(postToUi);

            WireEventPipelineUiHealth(pipeline, dispatcher, new MonitoringHealthState(viewModel, postToUi));
        }

        internal static void WireEventPipelineUiHealth(
            EventPipeline pipeline,
            FastEventDispatcher dispatcher,
            MonitoringHealthState health)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(health);

            pipeline.TerminalSourceFaulted += _ => health.RecordTerminalFault();
            dispatcher.TerminalStatusRecorded += _ => health.RecordTerminalFault();
        }

        internal static void ReportTerminalFaultUiHealth(
            MainViewModel viewModel,
            Action<Action> postToUi)
        {
            try
            {
                postToUi(() => viewModel.SetStatus("Monitoring error"));
            }
            catch
            {
                // UI health reporting must not affect source or module fault handling.
            }
        }

        internal static void WireIdentityActivationUiHealth(
            PlayerProfileIdentityResolver resolver,
            MainViewModel viewModel,
            Action<Action> postToUi,
            RuntimeDiagnosticService diagnostics)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            ArgumentNullException.ThrowIfNull(viewModel);
            ArgumentNullException.ThrowIfNull(postToUi);
            ArgumentNullException.ThrowIfNull(diagnostics);

            WireIdentityActivationUiHealth(resolver, new MonitoringHealthState(viewModel, postToUi), diagnostics);
        }

        internal static void WireIdentityActivationUiHealth(
            PlayerProfileIdentityResolver resolver,
            MonitoringHealthState health,
            RuntimeDiagnosticService diagnostics)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            ArgumentNullException.ThrowIfNull(health);
            ArgumentNullException.ThrowIfNull(diagnostics);

            void Refresh() => health.UpdateIdentity(resolver.GetActivationStatusSnapshot());
            void Report(PlayerProfileIdentityActivationStatus status)
            {
                if (status.FailureReason is null)
                    return;
                diagnostics.Report(
                    RuntimeDiagnosticLevel.Warning,
                    nameof(PlayerProfileIdentityResolver),
                    "Identity source activation was blocked: {0}.",
                    [status.FailureReason]);
            }

            resolver.ActivationStatusChanged += (_, status) =>
            {
                Report(status);
                Refresh();
            };
            foreach (var status in resolver.GetActivationStatusSnapshot().Where(status => status.FailureReason is not null))
                Report(status);
            Refresh();
        }

        internal static void ApplyIdentityActivationUiHealth(
            PlayerProfileIdentityResolver resolver,
            MainViewModel viewModel)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            ArgumentNullException.ThrowIfNull(viewModel);

            var statuses = resolver.GetActivationStatusSnapshot();
            if (statuses.Any(status => status.IsRestartRequired))
                viewModel.SetStatus("Monitoring - Event owner changed; restart required");
            else if (statuses.Any(status => status.IsOwnershipConflict))
                viewModel.SetStatus("Monitoring - ownership conflict; Event paused");
            else if (!statuses.Any(status => status.HasIssue))
                viewModel.SetStatus("Monitoring");
            else
                viewModel.SetStatus(statuses.Any(status => status.IsActive)
                    ? "Monitoring identity warning"
                    : "Monitoring identity error");
        }

        private void ReportSourceStatus(
            RuntimeDiagnosticLevel level,
            string eventName,
            EventPipelineSourceStatus status) =>
            _diagnostics.ReportProfile(
                level,
                nameof(EventPipeline),
                status.ProfileName,
                eventName,
                $"Segment={System.IO.Path.GetFileName(status.CanonicalPath)} Generation={status.SourceGeneration}");

        private void ShutdownEventPipeline()
        {
            if (_eventPipeline is null || _eventDispatcher is null)
                return;

            try
            {
                _eventPipeline.DisposeAndCompleteAsync(_eventDispatcher).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                _diagnostics.Report(RuntimeDiagnosticLevel.Error, nameof(App),
                    "Event Pipeline shutdown completed with aggregated fault(s): {0}", exception);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            PrepareForShutdown();
            base.OnExit(e);
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            base.OnSessionEnding(e);
            if (!e.Cancel)
                PrepareForShutdown();
        }

        private void RequestShutdown() => RequestShutdown(PrepareForShutdown, Shutdown);

        internal static void RequestApplicationShutdown()
        {
            if (Application.Current is App app)
                app.RequestShutdown();
            else
                Application.Current.Shutdown();
        }

        internal static void RequestShutdown(Action prepareForShutdown, Action shutdown)
        {
            ArgumentNullException.ThrowIfNull(prepareForShutdown);
            ArgumentNullException.ThrowIfNull(shutdown);

            prepareForShutdown();
            shutdown();
        }

        private void PrepareForShutdown()
        {
            if (Interlocked.Exchange(ref _shutdownPrepared, 1) != 0)
                return;

            _monitoringHealth?.Close();
            StopSnapshotReconciliation();

            _diagnostics.Report(RuntimeDiagnosticLevel.Info, nameof(App), "Application shutting down.");
            RunShutdownActions(_diagnostics,
                ("MainViewModelLiveSkillUpdates", () => _mainViewModel?.DetachLiveSkillUpdates()),
                ("MainViewModelAnalyseUpdates", () => _mainViewModel?.DetachAnalyseUpdates()),
                ("LiveSkillMonitor", () => _liveSkillMonitor?.Dispose()),
                ("EventPipeline", ShutdownEventPipeline),
                ("EventLineCoordinator", () => _eventLineCoordinator?.Dispose()),
                ("EventAlertDelivery", () => _eventAlertDelivery?.Dispose()),
                ("EventAlertPopupHost", () => _eventAlertPopupHost?.Dispose()),
                ("TrayIconService", () => _trayIconService?.Dispose()),
                ("SingleInstanceService", () => _singleInstanceService?.Dispose()));
        }

        private void StartSnapshotReconciliation()
        {
            if (_logDiscoverySnapshots is null || _eventPipeline is null || _liveSkillMonitor is null)
                return;

            lock (_snapshotReconciliationSync)
            {
                if (_snapshotReconciliationSubscribed)
                    return;
                _snapshotReconciliationCancellation = new CancellationTokenSource();
                _snapshotReconciliationSubscribed = true;
                _logDiscoverySnapshots.SnapshotPublished += OnDiscoverySnapshotPublished;
            }

            // Subscribe before reading current state so a publication between
            // startup discovery and subscription cannot be lost.
            var publication = _logDiscoverySnapshots.GetCurrentSnapshot();
            if (publication is not null)
                QueueSnapshotReconciliation(publication);
        }

        private void OnDiscoverySnapshotPublished(object? sender, LogDiscoverySnapshotPublication publication) =>
            QueueSnapshotReconciliation(publication);

        private void QueueSnapshotReconciliation(LogDiscoverySnapshotPublication publication)
        {
            lock (_snapshotReconciliationSync)
            {
                if (!_snapshotReconciliationSubscribed
                    || publication.Version <= _lastQueuedSnapshotVersion)
                    return;

                _lastQueuedSnapshotVersion = publication.Version;
                var previous = _snapshotReconciliationTail;
                _snapshotReconciliationTail = Task.Run(() => ReconcileSnapshotAfterAsync(previous, publication,
                    _snapshotReconciliationCancellation!.Token));
            }
        }

        private async Task ReconcileSnapshotAfterAsync(Task previous, LogDiscoverySnapshotPublication publication,
            CancellationToken cancellationToken)
        {
            try { await previous.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(App),
                    "Previous discovery refresh reconciliation failed: {0}", exception.GetType().Name);
            }

            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _shutdownPrepared) != 0)
                return;

            if (_eventPipeline is null || _liveSkillMonitor is null)
                return;

            ReconcileDiscoverySnapshot(publication, _eventPipeline, _liveSkillMonitor, _snapshotRetirements);
        }

        // Runs only in the existing serialized SnapshotPublished tail. It does
        // not perform I/O or await FIFO drain; EventPipeline's own cycle owns
        // the latter and keeps other sources polling.
        internal static void ReconcileDiscoverySnapshot(LogDiscoverySnapshotPublication publication,
            EventPipeline eventPipeline, LiveSkillMonitor liveSkillMonitor,
            IDictionary<string, SnapshotRetirementState> retirements)
        {
            ArgumentNullException.ThrowIfNull(publication);
            ArgumentNullException.ThrowIfNull(eventPipeline);
            ArgumentNullException.ThrowIfNull(liveSkillMonitor);
            ArgumentNullException.ThrowIfNull(retirements);

            // Preserve LD-3E1 add-only refresh before any reconciliation.
            eventPipeline.PublishDiscoverySnapshot(publication.Version, publication.Snapshot.SourceCandidates,
                publication.Snapshot.Discovery.SourceOwnershipAmbiguities,
                publication.Snapshot.IsComplete && publication.Snapshot.Discovery.Uncertainties.Count == 0
                && publication.Snapshot.Discovery.Rejections.Count == 0);
            eventPipeline.AddSourceCandidates(publication.Version, publication.Snapshot.SourceCandidates);
            liveSkillMonitor.AddSourceCandidates(publication.Version, publication.Snapshot.SourceCandidates);

            var plan = publication.Snapshot.Reconciliation;
            if (!plan.IsComplete || plan.SnapshotVersion != publication.Version)
                return;

            foreach (var intent in plan.SuppressionIntents)
            {
                var skillsAccepted = liveSkillMonitor.TryRetireSource(intent.PhysicalDirectoryKey,
                    intent.Generation, intent.SnapshotVersion);
                var eventAccepted = eventPipeline.TryRequestControlledRetirement(intent.PhysicalDirectoryKey,
                    intent.Generation, intent.SnapshotVersion, snapshotIsComplete: true);
                if (skillsAccepted || eventAccepted)
                    retirements[intent.PhysicalDirectoryKey] = new SnapshotRetirementState(intent.Generation,
                        intent.SnapshotVersion, skillsAccepted, eventAccepted);
            }

            foreach (var current in plan.Current)
            {
                if (!retirements.TryGetValue(current.PhysicalDirectoryKey, out var retirement)
                    || current.Generation <= retirement.Generation)
                    continue;

                if (retirement.SkillsRetired && liveSkillMonitor.TryPermitReappearance(current.PhysicalDirectoryKey,
                        retirement.Generation, current.Generation, publication.Version))
                    retirement = retirement with { SkillsRetired = false };

                if (retirement.EventRetired && eventPipeline.TryPermitControlledReappearance(current.PhysicalDirectoryKey,
                        retirement.Generation, current.Generation, publication.Version))
                    retirement = retirement with { PendingEventReappearanceGeneration = current.Generation };

                // Skills can forget a completed suppression. Event keeps the
                // record until a later absence can cancel an in-flight permit.
                if (!retirement.SkillsRetired && !retirement.EventRetired)
                    retirements.Remove(current.PhysicalDirectoryKey);
                else
                    retirements[current.PhysicalDirectoryKey] = retirement;
            }
        }

        internal sealed record SnapshotRetirementState(long Generation, long SnapshotVersion,
            bool SkillsRetired, bool EventRetired, long? PendingEventReappearanceGeneration = null);

        private void StopSnapshotReconciliation()
        {
            Task tail;
            CancellationTokenSource? cancellation;
            lock (_snapshotReconciliationSync)
            {
                if (!_snapshotReconciliationSubscribed)
                    return;
                _snapshotReconciliationSubscribed = false;
                if (_logDiscoverySnapshots is not null)
                    _logDiscoverySnapshots.SnapshotPublished -= OnDiscoverySnapshotPublished;
                cancellation = _snapshotReconciliationCancellation;
                _snapshotReconciliationCancellation = null;
                cancellation?.Cancel();
                tail = _snapshotReconciliationTail;
            }

            try { tail.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            finally { cancellation?.Dispose(); }
        }

        private void ActivatePrimaryInstance()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            try
            {
                _ = Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (MainWindow is MainWindow mainWindow)
                        mainWindow.ShowAndActivate();
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        internal static bool TryRunStartupPipeline(Action startupPipeline)
            => TryRunStartupPipeline(startupPipeline, null);

        private static bool TryRunStartupPipeline(
            Action startupPipeline,
            RuntimeDiagnosticService? diagnostics)
        {
            ArgumentNullException.ThrowIfNull(startupPipeline);

            try
            {
                startupPipeline();
                return true;
            }
            catch (Exception exception)
            {
                diagnostics?.Report(RuntimeDiagnosticLevel.Error, nameof(App), "Application startup pipeline failed: {0}", exception);
                return false;
            }
        }

        internal static void RunShutdownActions(params (string ServiceName, Action Action)[] actions)
            => RunShutdownActions(null, actions);

        private static void RunShutdownActions(
            RuntimeDiagnosticService? diagnostics,
            params (string ServiceName, Action Action)[] actions)
        {
            ArgumentNullException.ThrowIfNull(actions);

            foreach (var (serviceName, action) in actions)
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    diagnostics?.Report(RuntimeDiagnosticLevel.Error, nameof(App), "Application shutdown failed for {0}: {1}", serviceName, exception);
                }
            }
        }

        private void HandleStartupFailure()
        {
            if (MainWindow is not MainWindow mainWindow
                || mainWindow.DataContext is not MainViewModel viewModel)
            {
                RequestShutdown();
                return;
            }

            viewModel.SetStatus("Startup error");
            mainWindow.SetTrayAvailable(false);
            if (!mainWindow.IsVisible)
                mainWindow.Show();
        }

        private bool ApplySettings(AppSettings settings, MainWindow mainWindow)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (!TryApplySettingsChanges(_settings, settings, _settingsService.Save))
                return false;

            mainWindow.SetAlwaysOnTop(_settings.AlwaysOnTop);
            _diagnostics.Enabled = _settings.DiagnosticsEnabled;
            return true;
        }

        internal static void ApplySettingsChanges(AppSettings currentSettings, AppSettings changedSettings)
        {
            ArgumentNullException.ThrowIfNull(currentSettings);
            ArgumentNullException.ThrowIfNull(changedSettings);

            currentSettings.AlwaysOnTop = changedSettings.AlwaysOnTop;
            currentSettings.StartMinimizedToTray = changedSettings.StartMinimizedToTray;
            currentSettings.DiagnosticsEnabled = changedSettings.DiagnosticsEnabled;
            currentSettings.EventAlerts = [.. changedSettings.EventAlerts];
            if (changedSettings.LocationSettingsChanged)
            {
                currentSettings.AdditionalWurmDataLocations = [.. changedSettings.AdditionalWurmDataLocations];
                currentSettings.KnownLogLocations = KnownLogLocations.CopyAll(changedSettings.KnownLogLocations);
            }
        }

        internal static bool TryApplySettingsChanges(
            AppSettings currentSettings,
            AppSettings changedSettings,
            Func<AppSettings, bool> saveSettings)
        {
            ArgumentNullException.ThrowIfNull(currentSettings);
            ArgumentNullException.ThrowIfNull(changedSettings);
            ArgumentNullException.ThrowIfNull(saveSettings);

            var candidate = new AppSettings
            {
                AlwaysOnTop = changedSettings.AlwaysOnTop,
                StartMinimizedToTray = changedSettings.StartMinimizedToTray,
                DiagnosticsEnabled = changedSettings.DiagnosticsEnabled,
                EventAlerts = [.. currentSettings.EventAlerts],
                AdditionalWurmDataLocations = ResolveAdditionalWurmDataLocations(currentSettings, changedSettings),
                KnownLogLocations = ResolveKnownLogLocations(currentSettings, changedSettings),
                LocationSettingsChanged = changedSettings.LocationSettingsChanged,
                WindowPlacement = currentSettings.WindowPlacement
            };
            if (!saveSettings(candidate))
                return false;

            ApplySettingsChanges(currentSettings, candidate);
            return true;
        }

        private static List<string> ResolveAdditionalWurmDataLocations(AppSettings currentSettings, AppSettings changedSettings)
        {
            if (!changedSettings.LocationSettingsChanged)
                return [.. currentSettings.AdditionalWurmDataLocations];

            var baseline = changedSettings.LocationSettingsBaselineAdditionalWurmDataLocations;
            if (baseline is null)
                return [.. changedSettings.AdditionalWurmDataLocations];

            var resolved = new List<string>(currentSettings.AdditionalWurmDataLocations);
            foreach (var removed in baseline.Except(changedSettings.AdditionalWurmDataLocations, StringComparer.OrdinalIgnoreCase))
                resolved.RemoveAll(path => string.Equals(path, removed, StringComparison.OrdinalIgnoreCase));
            foreach (var added in changedSettings.AdditionalWurmDataLocations.Except(baseline, StringComparer.OrdinalIgnoreCase))
            {
                if (!resolved.Contains(added, StringComparer.OrdinalIgnoreCase))
                    resolved.Add(added);
            }
            return resolved;
        }

        private static List<KnownLogLocation> ResolveKnownLogLocations(AppSettings currentSettings, AppSettings changedSettings)
        {
            if (!changedSettings.LocationSettingsChanged)
                return KnownLogLocations.CopyAll(currentSettings.KnownLogLocations);

            var baseline = changedSettings.LocationSettingsBaselineKnownLogLocations;
            if (baseline is null)
                return KnownLogLocations.CopyAll(changedSettings.KnownLogLocations);

            var resolved = KnownLogLocations.CopyAll(currentSettings.KnownLogLocations);
            foreach (var changed in changedSettings.KnownLogLocations)
            {
                var original = baseline.FirstOrDefault(location => string.Equals(location.Id, changed.Id, StringComparison.Ordinal));
                if (original is null || original.Enabled == changed.Enabled)
                    continue;

                var current = resolved.FirstOrDefault(location => string.Equals(location.Id, changed.Id, StringComparison.Ordinal));
                if (current is not null)
                    current.Enabled = changed.Enabled;
            }
            return resolved;
        }

        private bool SaveAlwaysOnTopSetting(bool enabled)
        {
            return TryApplySettingsChanges(
                _settings,
                new AppSettings
                {
                    AlwaysOnTop = enabled,
                    StartMinimizedToTray = _settings.StartMinimizedToTray,
                    DiagnosticsEnabled = _settings.DiagnosticsEnabled,
                    AdditionalWurmDataLocations = [.. _settings.AdditionalWurmDataLocations],
                    KnownLogLocations = KnownLogLocations.CopyAll(_settings.KnownLogLocations),
                },
                _settingsService.Save);
        }

        private void SaveWindowPlacement(WindowPlacement placement)
        {
            if (!TrySaveWindowPlacement(_settings, placement, _settingsService.Save))
                _mainViewModel?.SetStatus("Settings save failed");
        }

        internal static bool TrySaveWindowPlacement(
            AppSettings currentSettings,
            WindowPlacement placement,
            Func<AppSettings, bool> saveSettings)
        {
            ArgumentNullException.ThrowIfNull(currentSettings);
            ArgumentNullException.ThrowIfNull(placement);
            ArgumentNullException.ThrowIfNull(saveSettings);

            var candidate = new AppSettings
            {
                AlwaysOnTop = currentSettings.AlwaysOnTop,
                StartMinimizedToTray = currentSettings.StartMinimizedToTray,
                DiagnosticsEnabled = currentSettings.DiagnosticsEnabled,
                EventAlerts = [.. currentSettings.EventAlerts],
                AdditionalWurmDataLocations = [.. currentSettings.AdditionalWurmDataLocations],
                KnownLogLocations = KnownLogLocations.CopyAll(currentSettings.KnownLogLocations),
                WindowPlacement = placement
            };
            if (!saveSettings(candidate))
                return false;

            currentSettings.WindowPlacement = placement;
            return true;
        }

        internal static void ApplySavedWindowPlacement(
            MainWindow mainWindow,
            WindowPlacement? placement,
            WindowPlacementService placementService)
        {
            ArgumentNullException.ThrowIfNull(mainWindow);
            ArgumentNullException.ThrowIfNull(placementService);

            if (placement is null
                || !placementService.IsValid(placement, mainWindow.MinWidth)
                || !placementService.IsVisibleOnAnyScreen(placement, mainWindow.MinHeight))
            {
                return;
            }

            mainWindow.ApplyPlacement(placement);
        }
    }

}
