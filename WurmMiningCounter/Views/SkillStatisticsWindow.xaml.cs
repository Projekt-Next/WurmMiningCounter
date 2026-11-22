using System.Windows;
using System.Windows.Threading;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Views;

public partial class SkillStatisticsWindow : Window
{
    private readonly SkillStatisticsViewModel _viewModel;
    private readonly DispatcherTimer _timer;

    public SkillStatisticsWindow(SkillStatisticsViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs) => _timer.Start();

    private void OnTimerTick(object? sender, EventArgs eventArgs) => _viewModel.RefreshTimeDerivedMetrics();

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _viewModel.DetachLiveSkillUpdates();
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
