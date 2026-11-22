using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Views;

public partial class ResetWindow : Window
{
    private readonly ResetSelectionViewModel _viewModel;

    public ResetWindow(ResetSelectionViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        var completed = await _viewModel.ConfirmAndExecuteAsync(message =>
            MessageBox.Show(
                this,
                message,
                "Confirm reset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes);

        if (completed)
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnSelectAllMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProfileResetOptionViewModel option })
            option.SelectAllMaterials();
    }

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
