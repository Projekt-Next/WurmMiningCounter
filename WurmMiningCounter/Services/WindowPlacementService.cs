using Forms = System.Windows.Forms;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

internal interface IWindowPlacementScreenProvider
{
    IReadOnlyList<WindowPlacementScreenBounds> GetScreenBounds();
}

internal readonly record struct WindowPlacementScreenBounds(
    double Left,
    double Top,
    double Width,
    double Height);

internal sealed class WindowsScreenPlacementProvider : IWindowPlacementScreenProvider
{
    public IReadOnlyList<WindowPlacementScreenBounds> GetScreenBounds()
    {
        return Forms.Screen.AllScreens
            .Select(screen => new WindowPlacementScreenBounds(
                screen.WorkingArea.Left,
                screen.WorkingArea.Top,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height))
            .ToArray();
    }
}

public sealed class WindowPlacementService
{
    private const double MinimumVisibleWidth = 64;
    private const double MinimumVisibleHeight = 32;

    private readonly IWindowPlacementScreenProvider _screenProvider;

    public WindowPlacementService()
        : this(new WindowsScreenPlacementProvider())
    {
    }

    internal WindowPlacementService(IWindowPlacementScreenProvider screenProvider)
    {
        _screenProvider = screenProvider ?? throw new ArgumentNullException(nameof(screenProvider));
    }

    public bool IsValid(WindowPlacement placement, double minWidth)
    {
        ArgumentNullException.ThrowIfNull(placement);

        if (!IsFinite(minWidth) || minWidth <= 0)
            return false;

        return IsFinite(placement.Left)
            && IsFinite(placement.Top)
            && IsFinite(placement.Width)
            && placement.Width > 0
            && placement.Width >= minWidth;
    }

    public bool IsVisibleOnAnyScreen(WindowPlacement placement, double windowHeight)
    {
        ArgumentNullException.ThrowIfNull(placement);

        if (!IsFinite(placement.Left)
            || !IsFinite(placement.Top)
            || !IsFinite(placement.Width)
            || !IsFinite(windowHeight)
            || placement.Width <= 0
            || windowHeight <= 0)
        {
            return false;
        }

        foreach (var screen in _screenProvider.GetScreenBounds())
        {
            if (!IsFinite(screen.Left)
                || !IsFinite(screen.Top)
                || !IsFinite(screen.Width)
                || !IsFinite(screen.Height)
                || screen.Width <= 0
                || screen.Height <= 0)
            {
                continue;
            }

            var visibleWidth = Math.Min(placement.Left + placement.Width, screen.Left + screen.Width)
                - Math.Max(placement.Left, screen.Left);
            var visibleHeight = Math.Min(placement.Top + windowHeight, screen.Top + screen.Height)
                - Math.Max(placement.Top, screen.Top);

            if (visibleWidth >= MinimumVisibleWidth && visibleHeight >= MinimumVisibleHeight)
                return true;
        }

        return false;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
