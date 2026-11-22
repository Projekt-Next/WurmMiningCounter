using System.Windows;
using System.Windows.Controls;
using MediaBrushes = System.Windows.Media.Brushes;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Views;

internal sealed class EventAlertPopupWindow : Window, IEventAlertPopup
{
    private event Action? PopupClosed;

    public EventAlertPopupWindow(EventAlertNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = MediaBrushes.White;
        Content = new Border
        {
            BorderBrush = MediaBrushes.DimGray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = notification.ProfileName,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 14
                    },
                    new TextBlock
                    {
                        Text = notification.Trigger.PopupText,
                        Margin = new Thickness(0, 4, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 280
                    }
                }
            }
        };
        Loaded += (_, _) => PositionAtBottomRight();
        Closed += (_, _) => PopupClosed?.Invoke();
    }

    event Action? IEventAlertPopup.Closed
    {
        add => PopupClosed += value;
        remove => PopupClosed -= value;
    }

    void IEventAlertPopup.Show() => Show();

    void IEventAlertPopup.Close() => Close();

    private void PositionAtBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }
}
