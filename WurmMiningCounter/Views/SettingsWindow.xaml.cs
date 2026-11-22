using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Views;

public partial class SettingsWindow : Window
{
    private readonly Func<bool> _resetSessionConfirmation;
    private readonly Func<bool> _diagnosticLogClearConfirmation;
    private readonly SettingsViewModel _viewModel;
    private readonly DispatcherTimer _diagnosticsRefreshTimer;
    private LogLocationsManagerWindow? _logLocationsManagerWindow;

    public string VersionText { get; } = GetVersionText();

    public SettingsWindow(SettingsViewModel viewModel)
        : this(viewModel, null)
    {
    }

    internal SettingsWindow(
        SettingsViewModel viewModel,
        Func<bool>? resetSessionConfirmation,
        Func<bool>? diagnosticLogClearConfirmation = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        _resetSessionConfirmation = resetSessionConfirmation ?? ConfirmResetSession;
        _diagnosticLogClearConfirmation = diagnosticLogClearConfirmation ?? (() => DiagnosticLogClearConfirmation.Confirm(this));
        InitializeComponent();
        DataContext = _viewModel;
        _diagnosticsRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _diagnosticsRefreshTimer.Tick += OnDiagnosticsRefreshTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    internal event EventHandler? ResetSessionConfirmed;
    internal event EventHandler? DiagnosticsWindowRequested;
    internal event EventHandler? DiagnosticLogClearRequested;

    internal void RequestResetSession()
    {
        if (_resetSessionConfirmation())
            ResetSessionConfirmed?.Invoke(this, EventArgs.Empty);
    }

    internal void RequestDiagnosticLogClear()
    {
        DiagnosticLogClearConfirmation.ClearIfConfirmed(
            _diagnosticLogClearConfirmation,
            () => DiagnosticLogClearRequested?.Invoke(this, EventArgs.Empty));
    }

    private void OnResetSessionClick(object sender, RoutedEventArgs e)
    {
        RequestResetSession();
    }

    private void OnOpenDiagnosticsWindowClick(object sender, RoutedEventArgs e)
    {
        DiagnosticsWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearDiagnosticLogClick(object sender, RoutedEventArgs e)
    {
        RequestDiagnosticLogClear();
    }

    private async void OnFindLocationsClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.FindLocationsAsync();
    }

    private void OnManageLogLocationsClick(object sender, RoutedEventArgs e)
    {
        if (_logLocationsManagerWindow is { IsLoaded: true })
        {
            _logLocationsManagerWindow.Activate();
            return;
        }

        var manager = new LogLocationsManagerWindow(_viewModel) { Owner = this };
        manager.Closed += OnLogLocationsManagerClosed;
        _logLocationsManagerWindow = manager;
        manager.Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshDiagnosticsState();
        _diagnosticsRefreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CancelLocationDiscovery();
        if (_logLocationsManagerWindow is not null)
        {
            _logLocationsManagerWindow.Closed -= OnLogLocationsManagerClosed;
            _logLocationsManagerWindow = null;
        }
        _diagnosticsRefreshTimer.Stop();
        _diagnosticsRefreshTimer.Tick -= OnDiagnosticsRefreshTimerTick;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }

    private void OnLogLocationsManagerClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_logLocationsManagerWindow, sender))
            _logLocationsManagerWindow = null;
    }

    private void OnDiagnosticsRefreshTimerTick(object? sender, EventArgs e) => _viewModel.RefreshDiagnosticsState();

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsCloseButtonSource(e.OriginalSource as DependencyObject))
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private bool ConfirmResetSession()
    {
        return MessageBox.Show(
            this,
            "Reset all mining counts for the current session?",
            "Reset Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static string GetVersionText()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataSeparatorIndex = informationalVersion.IndexOf('+');
            var displayVersion = metadataSeparatorIndex >= 0
                ? informationalVersion[..metadataSeparatorIndex]
                : informationalVersion;
            return $"v{displayVersion}";
        }

        return $"v{assembly.GetName().Version}";
    }

    private static bool IsCloseButtonSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
