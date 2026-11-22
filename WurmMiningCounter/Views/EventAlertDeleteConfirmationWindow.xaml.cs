using System.Windows;

namespace WurmMiningCounter.Views;

public partial class EventAlertDeleteConfirmationWindow : Window
{
    public EventAlertDeleteConfirmationWindow(string triggerName)
    {
        InitializeComponent();
        DataContext = $"Are you sure you want to delete \"{triggerName}\"?";
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
