using System.Windows;

namespace WurmMiningCounter.Views;

internal static class EventAlertDeleteConfirmation
{
    public static bool Confirm(Window owner, string triggerName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);

        return new EventAlertDeleteConfirmationWindow(triggerName)
        {
            Owner = owner
        }.ShowDialog() == true;
    }
}
