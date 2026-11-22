using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WurmMiningCounter.ViewModels;
using Forms = System.Windows.Forms;

namespace WurmMiningCounter.Views;

public partial class LogLocationsManagerWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public LogLocationsManagerWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void OnFindLocationsClick(object sender, RoutedEventArgs e) => await _viewModel.FindLocationsAsync();

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a Wurm data, players, or logs folder."
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            _viewModel.AddAdditionalWurmDataLocation(dialog.SelectedPath);
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs e) => _viewModel.RemoveSelectedAdditionalWurmDataLocation();

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsCloseButtonSource(e.OriginalSource as DependencyObject))
            return;

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e) => Close();

    private static bool IsCloseButtonSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
