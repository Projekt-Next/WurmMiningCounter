using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Views;

public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DiagnosticsLogFileService _logFileService;
    private readonly Func<bool> _diagnosticLogClearConfirmation;

    public DiagnosticsWindow(DiagnosticsViewModel viewModel)
        : this(viewModel, new DiagnosticsLogFileService())
    {
    }

    internal DiagnosticsWindow(
        DiagnosticsViewModel viewModel,
        DiagnosticsLogFileService logFileService,
        Func<bool>? diagnosticLogClearConfirmation = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _logFileService = logFileService ?? throw new ArgumentNullException(nameof(logFileService));
        _diagnosticLogClearConfirmation = diagnosticLogClearConfirmation ?? (() => DiagnosticLogClearConfirmation.Confirm(this));
        InitializeComponent();
        DataContext = _viewModel;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshSnapshot();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e) => _viewModel.RefreshSnapshot();

    internal bool RequestClear() => DiagnosticLogClearConfirmation.ClearIfConfirmed(
        _diagnosticLogClearConfirmation,
        _viewModel.Clear);

    private void OnClearClick(object sender, RoutedEventArgs e) => RequestClear();

    private void OnCopySelectedClick(object sender, RoutedEventArgs e) => CopySelected();

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(_viewModel.CreateCopyText());
    }

    private void OnDiagnosticsGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        CopySelected();
        e.Handled = true;
    }

    private void CopySelected()
    {
        var selectedEntries = DiagnosticsGrid.SelectedItems
            .Cast<RuntimeDiagnosticEntry>()
            .OrderBy(entry => DiagnosticsGrid.Items.IndexOf(entry))
            .ToArray();
        if (selectedEntries.Length == 0)
            return;

        CopyToClipboard(CreateCopyText(selectedEntries));
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard availability must not affect diagnostics or the app.
        }
    }

    internal static string CreateCopyText(IReadOnlyList<RuntimeDiagnosticEntry> entries) =>
        DiagnosticsLogFileService.Format(entries);

    private void OnSaveLogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = DiagnosticsLogFileService.CreateDefaultFileName(DateTime.Now),
            DefaultExt = ".txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (_logFileService.TrySave(dialog.FileName, _viewModel.GetSnapshot(), out var errorMessage))
            return;

        MessageBox.Show(
            this,
            $"The diagnostics log could not be saved.\n\n{errorMessage}",
            "Save Diagnostics Log",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
