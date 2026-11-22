using System.Drawing;
using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WurmMiningCounter.Models;
using WurmMiningCounter.ViewModels;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Services;

internal readonly record struct SettingsWindowWorkingArea(
    double Left,
    double Top,
    double Width,
    double Height);

internal interface ISettingsWindowWorkingAreaProvider
{
    SettingsWindowWorkingArea GetWorkingArea(MainWindow mainWindow);
}

internal sealed class WindowsSettingsWindowWorkingAreaProvider : ISettingsWindowWorkingAreaProvider
{
    public SettingsWindowWorkingArea GetWorkingArea(MainWindow mainWindow)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);

        var handle = new WindowInteropHelper(mainWindow).Handle;
        var screen = handle == IntPtr.Zero
            ? Forms.Screen.PrimaryScreen
            : Forms.Screen.FromHandle(handle);
        var workingArea = screen?.WorkingArea
            ?? throw new InvalidOperationException("The current screen working area could not be determined.");
        var dpi = VisualTreeHelper.GetDpi(mainWindow);

        return new SettingsWindowWorkingArea(
            workingArea.Left / dpi.DpiScaleX,
            workingArea.Top / dpi.DpiScaleY,
            workingArea.Width / dpi.DpiScaleX,
            workingArea.Height / dpi.DpiScaleY);
    }
}

internal interface ITrayIconResource : IDisposable
{
    bool Visible { get; set; }
}

internal sealed class NotifyIconResource(Forms.NotifyIcon notifyIcon) : ITrayIconResource
{
    public bool Visible
    {
        get => notifyIcon.Visible;
        set => notifyIcon.Visible = value;
    }

    public void Dispose() => notifyIcon.Dispose();
}

public sealed class TrayIconService : IDisposable
{
    private const double SettingsWindowScreenMargin = 12;

    private readonly object _sync = new();
    private readonly MainWindow _mainWindow;
    private readonly MainViewModel _viewModel;
    private readonly Dispatcher _dispatcher;
    private readonly Func<Icon, Forms.ContextMenuStrip, Action<Forms.MouseEventArgs>, ITrayIconResource> _trayIconResourceFactory;
    private readonly Func<Icon> _applicationIconLoader;
    private readonly Func<SettingsViewModel> _settingsViewModelFactory;
    private readonly Func<SettingsViewModel, SettingsWindow> _settingsWindowFactory;
    private readonly Func<AppSettings, bool> _settingsSaved;
    private readonly Func<bool, bool> _alwaysOnTopChanged;
    private readonly Action _shutdown;
    private readonly ISettingsWindowWorkingAreaProvider _settingsWindowWorkingAreaProvider;
    private readonly RuntimeDiagnosticService _diagnostics;
    private readonly Action _alertsRequested;
    private readonly Func<AlertsWindow>? _alertsWindowFactory;

    private ITrayIconResource? _notifyIcon;
    private Forms.ContextMenuStrip? _menu;
    private Forms.ToolStripMenuItem? _alwaysOnTopItem;
    private Icon? _applicationIcon;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsViewModel;
    private DiagnosticsWindow? _diagnosticsWindow;
    private readonly Func<SkillStatisticsWindow>? _skillStatisticsWindowFactory;
    private SkillStatisticsWindow? _skillStatisticsWindow;
    private PlayerProfileId? _lastSkillStatisticsProfileId;
    private AlertsWindow? _alertsWindow;
    private int _isDisposed;

    public TrayIconService(
        MainWindow mainWindow,
        MainViewModel viewModel,
        Func<SettingsViewModel> settingsViewModelFactory,
        Func<AppSettings, bool> settingsSaved,
        Func<bool, bool> alwaysOnTopChanged,
        RuntimeDiagnosticService? diagnostics = null,
        Func<SkillStatisticsWindow>? skillStatisticsWindowFactory = null,
        Action? alertsRequested = null,
        Func<AlertsWindow>? alertsWindowFactory = null,
        Action? shutdown = null)
        : this(
            mainWindow,
            viewModel,
            mainWindow.Dispatcher,
            CreateNotifyIconResource,
            LoadApplicationIcon,
            settingsViewModelFactory,
            viewModel => new SettingsWindow(viewModel),
            settingsSaved,
            alwaysOnTopChanged,
            shutdown ?? WurmMiningCounter.App.RequestApplicationShutdown,
            diagnostics: diagnostics,
            skillStatisticsWindowFactory: skillStatisticsWindowFactory,
            alertsRequested: alertsRequested,
            alertsWindowFactory: alertsWindowFactory)
    {
    }

    internal TrayIconService(
        MainWindow mainWindow,
        MainViewModel viewModel,
        Dispatcher dispatcher,
        Func<Icon, Forms.ContextMenuStrip, Action<Forms.MouseEventArgs>, ITrayIconResource> trayIconResourceFactory,
        Func<Icon>? applicationIconLoader = null,
        Func<SettingsViewModel>? settingsViewModelFactory = null,
        Func<SettingsViewModel, SettingsWindow>? settingsWindowFactory = null,
        Func<AppSettings, bool>? settingsSaved = null,
        Func<bool, bool>? alwaysOnTopChanged = null,
        Action? shutdown = null,
        ISettingsWindowWorkingAreaProvider? settingsWindowWorkingAreaProvider = null,
        RuntimeDiagnosticService? diagnostics = null,
        Func<SkillStatisticsWindow>? skillStatisticsWindowFactory = null,
        Action? alertsRequested = null,
        Func<AlertsWindow>? alertsWindowFactory = null)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _trayIconResourceFactory = trayIconResourceFactory
            ?? throw new ArgumentNullException(nameof(trayIconResourceFactory));
        _applicationIconLoader = applicationIconLoader ?? LoadApplicationIcon;
        _settingsViewModelFactory = settingsViewModelFactory
            ?? (() => new SettingsViewModel(new AppSettings()));
        _settingsWindowFactory = settingsWindowFactory ?? (viewModel => new SettingsWindow(viewModel));
        _settingsSaved = settingsSaved ?? (_ => true);
        _alwaysOnTopChanged = alwaysOnTopChanged ?? (_ => true);
        _shutdown = shutdown ?? WurmMiningCounter.App.RequestApplicationShutdown;
        _settingsWindowWorkingAreaProvider = settingsWindowWorkingAreaProvider
            ?? new WindowsSettingsWindowWorkingAreaProvider();
        _diagnostics = diagnostics ?? new RuntimeDiagnosticService();
        _skillStatisticsWindowFactory = skillStatisticsWindowFactory;
        _alertsRequested = alertsRequested ?? (() => { });
        _alertsWindowFactory = alertsWindowFactory;
    }

    public void Initialize()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _notifyIcon is not null)
                return;

            Forms.ContextMenuStrip? menu = null;
            Icon? icon = null;
            try
            {
                menu = CreateMenu();
                icon = _applicationIconLoader();
                _notifyIcon = _trayIconResourceFactory(icon, menu, OnTrayIconMouseClick);
                _menu = menu;
                _applicationIcon = icon;
                _notifyIcon.Visible = true;
            }
            catch
            {
                if (_notifyIcon is not null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }

                menu?.Dispose();
                icon?.Dispose();
                _notifyIcon = null;
                throw;
            }
        }
    }

    public void Dispose()
    {
        ITrayIconResource? notifyIcon;
        Forms.ContextMenuStrip? menu;
        Icon? applicationIcon;
        SettingsWindow? settingsWindow;
        SettingsViewModel? settingsViewModel;
        DiagnosticsWindow? diagnosticsWindow;
        SkillStatisticsWindow? skillStatisticsWindow;
        AlertsWindow? alertsWindow;

        lock (_sync)
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            notifyIcon = _notifyIcon;
            _notifyIcon = null;
            menu = _menu;
            _menu = null;
            applicationIcon = _applicationIcon;
            _applicationIcon = null;
            _alwaysOnTopItem = null;
            settingsWindow = _settingsWindow;
            _settingsWindow = null;
            settingsViewModel = _settingsViewModel;
            _settingsViewModel = null;
            diagnosticsWindow = _diagnosticsWindow;
            _diagnosticsWindow = null;
            skillStatisticsWindow = _skillStatisticsWindow;
            _skillStatisticsWindow = null;
            alertsWindow = _alertsWindow;
            _alertsWindow = null;
        }

        DetachSettingsWindowHandlers(settingsWindow, settingsViewModel);
        if (diagnosticsWindow is not null)
            diagnosticsWindow.Close();
        if (skillStatisticsWindow is not null)
            skillStatisticsWindow.Close();
        if (alertsWindow is not null)
            alertsWindow.Close();

        if (notifyIcon is not null)
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
        }

        menu?.Dispose();
        applicationIcon?.Dispose();
    }

    private Forms.ContextMenuStrip CreateMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        _alwaysOnTopItem = new Forms.ToolStripMenuItem("Always On Top") { CheckOnClick = false };
        _alwaysOnTopItem.Click += (_, _) => ToggleAlwaysOnTop();
        menu.Items.Add(_alwaysOnTopItem);
        menu.Items.Add("Alerty", null, (_, _) => ShowAlerts());
        menu.Items.Add("Analyse", null, (_, _) => RunOnDispatcher(_mainWindow.ShowAnalyseFromTray));
        if (_skillStatisticsWindowFactory is not null)
            menu.Items.Add("Statistics", null, (_, _) => ShowSkillStatisticsWindow());
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        menu.Opening += (_, _) => RunOnDispatcher(UpdateAlwaysOnTopState);
        return menu;
    }

    private void ShowAlerts() => RunOnDispatcher(() =>
    {
        if (_alertsWindowFactory is null)
        {
            _alertsRequested();
            return;
        }

        if (_alertsWindow is not null)
        {
            _alertsWindow.WindowState = System.Windows.WindowState.Normal;
            _alertsWindow.Activate();
            return;
        }

        var window = _alertsWindowFactory();
        window.Closed += OnAlertsWindowClosed;
        _alertsWindow = window;
        window.Show();
        window.Activate();
    });

    private void OnAlertsWindowClosed(object? sender, EventArgs e)
    {
        if (_alertsWindow is not AlertsWindow window || !ReferenceEquals(window, sender))
            return;

        window.Closed -= OnAlertsWindowClosed;
        _alertsWindow = null;
    }

    private void ToggleMainWindowVisibility() => RunOnDispatcher(() =>
    {
        if (_mainWindow.IsVisible
            && _mainWindow.WindowState != System.Windows.WindowState.Minimized)
        {
            _mainWindow.Hide();
            return;
        }

        _mainWindow.ShowAndActivate();
    });

    private void OnTrayIconMouseClick(Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
            ToggleMainWindowVisibility();
    }

    private void ToggleAlwaysOnTop() => RunOnDispatcher(() =>
    {
        var previousValue = _mainWindow.Topmost;
        _mainWindow.ToggleAlwaysOnTop();
        if (!_alwaysOnTopChanged(_mainWindow.Topmost))
        {
            _mainWindow.SetAlwaysOnTop(previousValue);
            _viewModel.SetStatus("Settings save failed");
        }

        UpdateAlwaysOnTopState();
    });

    private void ShowSettings() => RunOnDispatcher(() =>
    {
        _mainWindow.ShowAndActivate();

        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.WindowState = System.Windows.WindowState.Normal;
            PositionWindowAtBottomRight(_settingsWindow);
            _settingsWindow.Activate();
            return;
        }

        var settingsViewModel = _settingsViewModelFactory();
        var settingsWindow = _settingsWindowFactory(settingsViewModel);
        settingsWindow.Closed += OnSettingsWindowClosed;
        settingsWindow.ResetSessionConfirmed += OnResetSessionConfirmed;
        settingsWindow.DiagnosticsWindowRequested += OnDiagnosticsWindowRequested;
        settingsWindow.DiagnosticLogClearRequested += OnDiagnosticLogClearRequested;
        settingsViewModel.PropertyChanged += OnSettingsViewModelPropertyChanged;
        _settingsWindow = settingsWindow;
        _settingsViewModel = settingsViewModel;

        PositionWindowAtBottomRight(settingsWindow);
        settingsWindow.Show();
        PositionWindowAtBottomRight(settingsWindow);
        settingsWindow.Activate();
    });

    private void ShowResetDialog() => RunOnDispatcher(() =>
    {
        _mainWindow.ShowAndActivate();
        var resetWindow = new ResetWindow(new ResetSelectionViewModel(_viewModel))
        {
            Owner = _mainWindow
        };
        resetWindow.ContentRendered += OnResetWindowContentRendered;
        resetWindow.SizeChanged += OnResetWindowSizeChanged;
        resetWindow.Closed += OnResetWindowClosed;
        resetWindow.ShowDialog();
    });

    private void OnResetWindowContentRendered(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Window resetWindow)
            PositionResetWindowAtBottomRight(resetWindow);
    }

    private void OnResetWindowSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (!e.HeightChanged
            || Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.01
            || sender is not System.Windows.Window resetWindow)
        {
            return;
        }

        PositionResetWindowVertically(resetWindow);
    }

    private void OnResetWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Window resetWindow)
            return;

        resetWindow.ContentRendered -= OnResetWindowContentRendered;
        resetWindow.SizeChanged -= OnResetWindowSizeChanged;
        resetWindow.Closed -= OnResetWindowClosed;
    }

    private void PositionWindowAtBottomRight(System.Windows.Window window)
    {
        var workingArea = _settingsWindowWorkingAreaProvider.GetWorkingArea(_mainWindow);
        var maximumLeft = workingArea.Left + Math.Max(0, workingArea.Width - window.Width);
        var maximumTop = workingArea.Top + Math.Max(0, workingArea.Height - window.Height);
        var requestedLeft = workingArea.Left + workingArea.Width - window.Width - SettingsWindowScreenMargin;
        var requestedTop = workingArea.Top + workingArea.Height - window.Height - SettingsWindowScreenMargin;

        window.Left = Math.Clamp(requestedLeft, workingArea.Left, maximumLeft);
        window.Top = Math.Clamp(requestedTop, workingArea.Top, maximumTop);
    }

    private void PositionResetWindowAtBottomRight(System.Windows.Window window)
    {
        var workingArea = _settingsWindowWorkingAreaProvider.GetWorkingArea(_mainWindow);
        var width = window.ActualWidth;
        var height = window.ActualHeight;

        if (width <= 0 || height <= 0)
            return;

        var maximumLeft = workingArea.Left + Math.Max(0, workingArea.Width - width);
        var maximumTop = workingArea.Top + Math.Max(0, workingArea.Height - height);
        var requestedLeft = workingArea.Left + workingArea.Width - width - SettingsWindowScreenMargin;
        var requestedTop = workingArea.Top + workingArea.Height - height - SettingsWindowScreenMargin;

        window.Left = Math.Clamp(requestedLeft, workingArea.Left, maximumLeft);
        window.Top = Math.Clamp(requestedTop, workingArea.Top, maximumTop);
    }

    private void PositionResetWindowVertically(System.Windows.Window window)
    {
        var workingArea = _settingsWindowWorkingAreaProvider.GetWorkingArea(_mainWindow);
        var height = window.ActualHeight;

        if (height <= 0)
            return;

        var maximumTop = workingArea.Top + Math.Max(0, workingArea.Height - height);
        var requestedTop = workingArea.Top + workingArea.Height - height - SettingsWindowScreenMargin;
        var top = Math.Clamp(requestedTop, workingArea.Top, maximumTop);

        if (Math.Abs(window.Top - top) >= 0.01)
            window.Top = top;
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_settingsWindow, sender))
            return;

        DetachSettingsWindowHandlers(_settingsWindow, _settingsViewModel);
        _settingsWindow = null;
        _settingsViewModel = null;
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SettingsViewModel settingsViewModel
            || (e.PropertyName != nameof(SettingsViewModel.AlwaysOnTop)
                && e.PropertyName != nameof(SettingsViewModel.StartMinimizedToTray)
                && e.PropertyName != nameof(SettingsViewModel.DiagnosticsEnabled)
                && e.PropertyName != nameof(SettingsViewModel.LocationSettingsChanged)))
        {
            return;
        }

        if (!_settingsSaved(settingsViewModel.CreateSettings()))
            _viewModel.SetStatus("Settings save failed");
        else if (e.PropertyName == nameof(SettingsViewModel.LocationSettingsChanged))
            settingsViewModel.MarkLocationSettingsSaved();
    }

    private void OnDiagnosticsWindowRequested(object? sender, EventArgs e) => RunOnDispatcher(ShowDiagnosticsWindow);

    private void ShowSkillStatisticsWindow() => RunOnDispatcher(() =>
    {
        if (_skillStatisticsWindow is { IsLoaded: true })
        {
            _skillStatisticsWindow.WindowState = System.Windows.WindowState.Normal;
            _skillStatisticsWindow.Activate();
            return;
        }

        var window = _skillStatisticsWindowFactory!();
        if (_lastSkillStatisticsProfileId is { } profileId
            && window.DataContext is SkillStatisticsViewModel viewModel
            && viewModel.Profiles.Any(profile => profile.ProfileId == profileId))
        {
            viewModel.SelectedProfileId = profileId;
        }

        window.Closed += OnSkillStatisticsWindowClosed;
        _skillStatisticsWindow = window;
        window.Show();
        window.Activate();
    });

    private void OnSkillStatisticsWindowClosed(object? sender, EventArgs e)
    {
        if (_skillStatisticsWindow is not SkillStatisticsWindow window || !ReferenceEquals(window, sender))
            return;

        _lastSkillStatisticsProfileId = (window.DataContext as SkillStatisticsViewModel)?.SelectedProfileId;
        window.Closed -= OnSkillStatisticsWindowClosed;
        _skillStatisticsWindow = null;
    }

    private void OnDiagnosticLogClearRequested(object? sender, EventArgs e)
    {
        _diagnostics.Clear();
        _settingsViewModel?.RefreshDiagnosticsState();
    }

    private void ShowDiagnosticsWindow()
    {
        if (_diagnosticsWindow is { IsLoaded: true })
        {
            _diagnosticsWindow.WindowState = System.Windows.WindowState.Normal;
            _diagnosticsWindow.Activate();
            return;
        }

        var diagnosticsWindow = new DiagnosticsWindow(new DiagnosticsViewModel(_diagnostics));
        diagnosticsWindow.Closed += OnDiagnosticsWindowClosed;
        _diagnosticsWindow = diagnosticsWindow;
        diagnosticsWindow.Show();
        diagnosticsWindow.Activate();
    }

    private void OnDiagnosticsWindowClosed(object? sender, EventArgs e)
    {
        if (_diagnosticsWindow is not DiagnosticsWindow diagnosticsWindow
            || !ReferenceEquals(diagnosticsWindow, sender))
            return;

        diagnosticsWindow.Closed -= OnDiagnosticsWindowClosed;
        _diagnosticsWindow = null;
    }

    private async void OnResetSessionConfirmed(object? sender, EventArgs e)
    {
        try
        {
            await _viewModel.ResetSessionAsync();
        }
        catch (Exception exception)
        {
            _diagnostics.Report(RuntimeDiagnosticLevel.Error, nameof(TrayIconService), "Tray reset session failed: {0}", exception);
        }
    }

    private void DetachSettingsWindowHandlers(
        SettingsWindow? settingsWindow,
        SettingsViewModel? settingsViewModel)
    {
        if (settingsWindow is not null)
        {
            settingsWindow.Closed -= OnSettingsWindowClosed;
            settingsWindow.ResetSessionConfirmed -= OnResetSessionConfirmed;
            settingsWindow.DiagnosticsWindowRequested -= OnDiagnosticsWindowRequested;
            settingsWindow.DiagnosticLogClearRequested -= OnDiagnosticLogClearRequested;
        }

        if (settingsViewModel is not null)
        {
            settingsViewModel.PropertyChanged -= OnSettingsViewModelPropertyChanged;
            settingsViewModel.Dispose();
        }
    }

    private void ExitApplication() => RunOnDispatcher(() =>
    {
        Dispose();
        _mainWindow.RequestExit();
        _shutdown();
    });

    private void UpdateAlwaysOnTopState()
    {
        if (_alwaysOnTopItem is not null)
            _alwaysOnTopItem.Checked = _mainWindow.Topmost;
    }

    private void RunOnDispatcher(Action action)
    {
        if (Volatile.Read(ref _isDisposed) != 0
            || _dispatcher.HasShutdownStarted
            || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        void InvokeAction()
        {
            if (Volatile.Read(ref _isDisposed) != 0)
                return;

            try
            {
                action();
            }
            catch (Exception exception)
            {
                _diagnostics.Report(RuntimeDiagnosticLevel.Error, nameof(TrayIconService), "Tray dispatcher action failed: {0}", exception);
            }
        }

        try
        {
            if (_dispatcher.CheckAccess())
                InvokeAction();
            else
                _ = _dispatcher.BeginInvoke((Action)InvokeAction);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static ITrayIconResource CreateNotifyIconResource(
        Icon icon,
        Forms.ContextMenuStrip menu,
        Action<Forms.MouseEventArgs> onMouseClick)
    {
        var notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Wurm Mining Counter",
            ContextMenuStrip = menu
        };
        notifyIcon.MouseClick += (_, e) => onMouseClick(e);
        return new NotifyIconResource(notifyIcon);
    }

    private static Icon LoadApplicationIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Resources/WurmMiningCounter.ico", UriKind.Absolute));

        if (resource is null)
            throw new InvalidOperationException("The application icon resource could not be loaded.");

        using var stream = resource.Stream;
        using var sourceIcon = new Icon(stream);
        return (Icon)sourceIcon.Clone();
    }
}
