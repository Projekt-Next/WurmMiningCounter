using System.Windows;

namespace WurmMiningCounter.Views;

public partial class DiagnosticLogClearConfirmationWindow : Window
{
    public DiagnosticLogClearConfirmationWindow()
    {
        InitializeComponent();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
