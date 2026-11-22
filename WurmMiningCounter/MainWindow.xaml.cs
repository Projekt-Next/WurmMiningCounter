using System.ComponentModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WurmMiningCounter.Models;
using WurmMiningCounter.ViewModels;
using WurmMiningCounter.Views;

namespace WurmMiningCounter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _placementDebounceTimer;
        private readonly Action _shutdown;
        private readonly Action<WindowPlacement>? _placementChanged;
        private ConditionalWeakTable<ContextMenu, ResetMenuContext> _resetMenuContexts = new();
        private bool _isTrayAvailable;
        private bool _isExitRequested;
        private bool _isApplyingPlacement;
        private bool _isSizeToContentRestorePending;
        private INotifyCollectionChanged? _observedProfilePanels;
        private WindowPlacement? _lastPublishedPlacement;
        private int _isShutdownRequested;
        private AnalyseResultsWindow? _analyseWindow;

        public MainWindow()
            : this(App.RequestApplicationShutdown, null)
        {
        }

        internal MainWindow(
            Action shutdown,
            Action<WindowPlacement>? placementChanged = null,
            TimeSpan? placementDebounceInterval = null)
        {
            _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
            _placementChanged = placementChanged;
            InitializeComponent();

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _refreshTimer.Tick += OnRefreshTimerTick;
            _placementDebounceTimer = new DispatcherTimer
            {
                Interval = placementDebounceInterval ?? TimeSpan.FromMilliseconds(500)
            };
            _placementDebounceTimer.Tick += OnPlacementDebounceTimerTick;
            Loaded += OnLoaded;
            Closed += OnClosed;
            LocationChanged += OnLocationChanged;
            SizeChanged += OnSizeChanged;
            IsVisibleChanged += OnWindowVisibilityChanged;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
                viewModel.Refresh();

            _refreshTimer.Start();
            AttachProfilePanelUpdates();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            AttachProfilePanelUpdates();
        }

        private void AttachProfilePanelUpdates()
        {
            if (_observedProfilePanels is not null)
                _observedProfilePanels.CollectionChanged -= OnProfilePanelsCollectionChanged;

            _observedProfilePanels = (DataContext as MainViewModel)?.ProfilePanels;
            if (_observedProfilePanels is not null)
                _observedProfilePanels.CollectionChanged += OnProfilePanelsCollectionChanged;
        }

        private void OnProfilePanelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isSizeToContentRestorePending || !IsLoaded)
                return;

            _isSizeToContentRestorePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _isSizeToContentRestorePending = false;
                if (IsLoaded)
                    SizeToContent = SizeToContent.Height;
            }));
        }

        private void OnRefreshTimerTick(object? sender, EventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
                viewModel.Refresh();
        }

        private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void OnStatusIndicatorMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnAnalyseClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ProfilePanelViewModel profile }
                || DataContext is not MainViewModel { AnalyseProcessor: not null })
                return;
            ShowAnalyse(profile.ProfileId, profile.ProfileName);
        }

        internal void ShowAnalyseFromTray()
        {
            if (_analyseWindow is not null)
            {
                ShowAnalyse(null, null);
                return;
            }
            var profiles = (DataContext as MainViewModel)?.ProfilePanels;
            var profile = profiles?.FirstOrDefault(item => item.HasAnalyseResult) ?? profiles?.FirstOrDefault();
            ShowAnalyse(profile?.ProfileId, profile?.ProfileName);
        }

        private void ShowAnalyse(PlayerProfileId? profileId, string? profileName)
        {
            if (DataContext is not MainViewModel { AnalyseProcessor: { } processor })
                return;
            if (_analyseWindow is null)
            {
                new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
                var window = new AnalyseResultsWindow(processor, profileId, profileName) { Owner = this };
                _analyseWindow = window;
                window.Closed += (_, _) => _analyseWindow = null;
                window.Show();
            }
            else
            {
                if (profileId is { } id)
                    _analyseWindow.SelectProfile(id, profileName!);
                if (_analyseWindow.WindowState == WindowState.Minimized)
                    _analyseWindow.WindowState = WindowState.Normal;
                _analyseWindow.Activate();
            }
        }

        private async void OnResetProspectingResultsClick(object sender, RoutedEventArgs e)
        {
            var menu = (sender as MenuItem)?.Parent as ContextMenu;
            if (menu is null || !_resetMenuContexts.TryGetValue(menu, out var context)
                || context.Operation != ResetOperation.ProspectingResults)
                return;

            await ConfirmAndResetProspectingResultsAsync(
                context.ProfileId,
                message => MessageBox.Show(
                    this,
                    message,
                    "Confirm reset",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes);
        }

        internal async Task<bool> ConfirmAndResetProspectingResultsAsync(
            PlayerProfileId profileId,
            Func<string, bool> confirm)
        {
            ArgumentNullException.ThrowIfNull(confirm);

            if (DataContext is not MainViewModel viewModel)
                throw new InvalidOperationException("The main window does not have a MainViewModel data context.");

            if (!viewModel.ProfilePanels.Any(panel =>
                panel.ProfileId == profileId && HasProspectingState(panel))
                || !confirm($"Reset Prospecting Results for {viewModel.ProfilePanels.First(panel => panel.ProfileId == profileId).ProfileName}?"))
                return false;

            if (DataContext is not MainViewModel currentViewModel
                || !currentViewModel.ProfilePanels.Any(panel =>
                    panel.ProfileId == profileId && HasProspectingState(panel)))
            {
                return false;
            }

            await currentViewModel.ResetVeinContextAsync(profileId);
            return true;
        }

        private void OnResetMaterialCounterClick(object sender, RoutedEventArgs e)
        {
            var menu = (sender as MenuItem)?.Parent as ContextMenu;
            if (menu is not null && _resetMenuContexts.TryGetValue(menu, out var capturedMaterialContext)
                && capturedMaterialContext.Operation == ResetOperation.Materials)
            {
                if (GetCurrentMaterialReset(menu) is { } current)
                {
                    ConfirmAndResetMaterialCounter(current.Profile, current.Material, message => MessageBox.Show(
                        this, message, "Confirm reset", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
                }
                return;
            }

            if (sender is not MenuItem { DataContext: MiningMaterialCountViewModel material }
                || sender is not FrameworkElement { Parent: ContextMenu { PlacementTarget: FrameworkElement target } }
                || !TryGetProfilePanel(target, out var profile))
            {
                return;
            }

            ConfirmAndResetMaterialCounter(
                profile,
                material,
                message => MessageBox.Show(
                    this,
                    message,
                    "Confirm reset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes);
        }

        private async void OnResetMiningInspirationClick(object sender, RoutedEventArgs e)
        {
            var menu = (sender as MenuItem)?.Parent as ContextMenu;
            if (menu is null || !_resetMenuContexts.TryGetValue(menu, out var context)
                || context.Operation != ResetOperation.MiningInspiration)
            {
                return;
            }

            await ConfirmAndResetMiningInspirationAsync(
                context.ProfileId,
                message => MessageBox.Show(
                    this,
                    message,
                    "Confirm reset",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes);
        }

        internal async Task<bool> ConfirmAndResetMiningInspirationAsync(
            PlayerProfileId profileId,
            Func<string, bool> confirm)
        {
            ArgumentNullException.ThrowIfNull(confirm);

            if (DataContext is not MainViewModel viewModel)
                throw new InvalidOperationException("The main window does not have a MainViewModel data context.");

            if (!viewModel.ProfilePanels.Any(panel => panel.ProfileId == profileId)
                || !confirm($"Reset Moment of Inspiration for {viewModel.ProfilePanels.First(panel => panel.ProfileId == profileId).ProfileName}?"))
            {
                return false;
            }

            if (DataContext is not MainViewModel currentViewModel
                || !currentViewModel.ProfilePanels.Any(panel => panel.ProfileId == profileId))
            {
                return false;
            }

            await currentViewModel.ResetMiningInspirationAsync(profileId);
            return true;
        }

        private void OnMaterialContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
            {
                ConfigureMaterialContextMenu(menu);
                CaptureMaterialResetMenuContext(menu);
            }
        }

        private void OnProspectingResultsContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
                CaptureProfileResetMenuContext(menu, ResetOperation.ProspectingResults);
        }

        private void OnMiningInspirationContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
                CaptureProfileResetMenuContext(menu, ResetOperation.MiningInspiration);
        }

        private void OnResetContextMenuClosed(object sender, RoutedEventArgs e)
        {
            // A Popup can close before MenuItem.Click. The next Opened replaces this context.
        }

        internal static void ConfigureMaterialContextMenu(ContextMenu menu)
        {
            ArgumentNullException.ThrowIfNull(menu);
            if (menu.PlacementTarget is not FrameworkElement
                {
                    DataContext: MiningMaterialCountViewModel material
                })
            {
                return;
            }

            menu.DataContext = material;
            if (menu.Items.OfType<MenuItem>().FirstOrDefault() is { } resetItem)
                resetItem.Header = $"Reset {material.DisplayName} counter";
        }

        internal bool ConfirmAndResetMaterialCounter(
            ProfilePanelViewModel profile,
            MiningMaterialCountViewModel material,
            Func<string, bool> confirm)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(material);
            ArgumentNullException.ThrowIfNull(confirm);

            if (!confirm(CreateMaterialResetConfirmation(material)))
                return false;

            if (DataContext is not MainViewModel viewModel)
                throw new InvalidOperationException("The main window does not have a MainViewModel data context.");

            viewModel.ResetMaterials(profile.ProfileId, [material.Material]);
            return true;
        }

        internal static string CreateMaterialResetConfirmation(MiningMaterialCountViewModel material)
        {
            ArgumentNullException.ThrowIfNull(material);

            return $"Reset {material.DisplayName} counter?\n\nCurrent value: {material.Count}\n\n"
                + "Total and other counters will not be affected.";
        }

        private static bool TryGetProfilePanel(DependencyObject source, out ProfilePanelViewModel profile)
        {
            for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement { DataContext: ProfilePanelViewModel panel })
                {
                    profile = panel;
                    return true;
                }
            }

            profile = null!;
            return false;
        }

        private void CaptureMaterialResetMenuContext(ContextMenu menu)
        {
            // Closed may precede the selected MenuItem.Click, so the previous context is
            // retained until this menu is opened again. A new opening must never fall back
            // to that previous interaction if its new target cannot be captured.
            _resetMenuContexts.Remove(menu);

            if (menu.PlacementTarget is not FrameworkElement target)
                return;

            if (target.DataContext is MiningMaterialCountViewModel material
                && TryGetProfilePanel(target, out var materialProfile))
            {
                _resetMenuContexts.Add(menu, new ResetMenuContext(
                    materialProfile.ProfileId,
                    material.Material,
                    ResetOperation.Materials));
            }
        }

        private void CaptureProfileResetMenuContext(ContextMenu menu, ResetOperation operation)
        {
            _resetMenuContexts.Remove(menu);

            if (menu.PlacementTarget is FrameworkElement { DataContext: ProfilePanelViewModel profile })
            {
                _resetMenuContexts.Add(menu, new ResetMenuContext(profile.ProfileId, null, operation));
            }
        }

        private static bool HasProspectingState(ProfilePanelViewModel panel) =>
            panel.HasProspectingResult || panel.HasNearbyScan;

        private (ProfilePanelViewModel Profile, MiningMaterialCountViewModel Material)? GetCurrentMaterialReset(ContextMenu? menu)
        {
            if (menu is null || !_resetMenuContexts.TryGetValue(menu, out var context)
                || context.Operation != ResetOperation.Materials || context.Material is not MiningMaterial materialId
                || DataContext is not MainViewModel viewModel)
            {
                return null;
            }

            var profile = viewModel.ProfilePanels.FirstOrDefault(panel => panel.ProfileId == context.ProfileId);
            var material = profile?.Materials.FirstOrDefault(candidate => candidate.Material == materialId);
            return profile is not null && material is not null ? (profile, material) : null;
        }

        private enum ResetOperation { Materials, ProspectingResults, MiningInspiration }

        private sealed record ResetMenuContext(
            PlayerProfileId ProfileId,
            MiningMaterial? Material,
            ResetOperation Operation);

        private void OnClosed(object? sender, EventArgs e)
        {
            _refreshTimer.Stop();
            _placementDebounceTimer.Stop();
            if (_observedProfilePanels is not null)
                _observedProfilePanels.CollectionChanged -= OnProfilePanelsCollectionChanged;
            _resetMenuContexts = new ConditionalWeakTable<ContextMenu, ResetMenuContext>();
            PublishPlacementIfEligible();

            if (!_isExitRequested && Interlocked.Exchange(ref _isShutdownRequested, 1) == 0)
                _shutdown();
        }

        public void SetTrayAvailable(bool available)
        {
            _isTrayAvailable = available;
        }

        public void SetAlwaysOnTop(bool enabled)
        {
            Topmost = enabled;
        }

        public void ApplyPlacement(WindowPlacement placement)
        {
            ArgumentNullException.ThrowIfNull(placement);

            _placementDebounceTimer.Stop();
            _isApplyingPlacement = true;
            try
            {
                Left = placement.Left;
                Top = placement.Top;
                Width = placement.Width;
            }
            finally
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    _placementDebounceTimer.Stop();
                    _lastPublishedPlacement = new WindowPlacement(Left, Top, Width);
                    _isApplyingPlacement = false;
                }));
            }
        }

        private void OnLocationChanged(object? sender, EventArgs e)
        {
            SchedulePlacementPublication();
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
                SchedulePlacementPublication();
        }

        private void SchedulePlacementPublication()
        {
            if (_isApplyingPlacement || !CanPublishPlacement())
                return;

            _placementDebounceTimer.Stop();
            _placementDebounceTimer.Start();
        }

        private void OnPlacementDebounceTimerTick(object? sender, EventArgs e)
        {
            _placementDebounceTimer.Stop();
            PublishPlacementIfEligible();
        }

        private void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible)
                _placementDebounceTimer.Stop();
        }

        private void PublishPlacementIfEligible()
        {
            if (!CanPublishPlacement())
                return;

            var placement = new WindowPlacement(Left, Top, Width);
            if (HasSameBounds(_lastPublishedPlacement, placement))
                return;

            _lastPublishedPlacement = placement;
            _placementChanged?.Invoke(placement);
        }

        private bool CanPublishPlacement()
        {
            return _placementChanged is not null
                && !_isApplyingPlacement
                && WindowState == WindowState.Normal
                && double.IsFinite(Left)
            && double.IsFinite(Top)
            && double.IsFinite(Width)
            && Width > 0;
        }

        private static bool HasSameBounds(WindowPlacement? first, WindowPlacement second)
        {
            return first is not null
                && first.Left == second.Left
                && first.Top == second.Top
                && first.Width == second.Width;
        }

        internal void ShowAndActivate()
        {
            if (!IsVisible)
                Show();

            WindowState = WindowState.Normal;
            Activate();
        }

        internal void ToggleAlwaysOnTop()
        {
            Topmost = !Topmost;
        }

        internal void RequestExit()
        {
            _isExitRequested = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isTrayAvailable && !_isExitRequested)
            {
                e.Cancel = true;
                Hide();
            }

            base.OnClosing(e);
            if (!e.Cancel)
                _analyseWindow?.Close();
        }
    }
}
