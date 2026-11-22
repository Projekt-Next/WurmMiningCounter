using System.Windows;

namespace WurmMiningCounter.Views;

internal static class DiagnosticLogClearConfirmation
{
    public static bool Confirm(Window owner)
    {
        var dialog = new DiagnosticLogClearConfirmationWindow
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true;
    }

    public static bool ClearIfConfirmed(Func<bool> confirmation, Action clear)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(clear);

        if (!confirmation())
            return false;

        clear();
        return true;
    }
}
