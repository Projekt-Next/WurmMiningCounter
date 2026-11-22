using System.Runtime.ExceptionServices;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

[Collection("WPF")]
public sealed class WindowPlacementStartupTests
{
    [Fact]
    public void ValidSavedPlacementRestoresOnlyLeftTopAndWidthBeforeFirstShow()
    {
        RunOnSta(() =>
        {
            var placement = new WindowPlacement(100, 150, 400);
            var window = new MainWindow();
            var originalHeight = window.Height;

            App.ApplySavedWindowPlacement(window, placement, CreateService(new WindowPlacementScreenBounds(0, 0, 1920, 1080)));

            Assert.Equal(placement.Left, window.Left);
            Assert.Equal(placement.Top, window.Top);
            Assert.Equal(placement.Width, window.Width);
            Assert.Equal(originalHeight, window.Height);
        });
    }

    [Fact]
    public void InvalidSavedPlacementIsIgnored()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var originalBounds = GetBounds(window);

            App.ApplySavedWindowPlacement(
                window,
                new WindowPlacement(100, 150, window.MinWidth - 1),
                CreateService(new WindowPlacementScreenBounds(0, 0, 1920, 1080)));

            Assert.Equal(originalBounds, GetBounds(window));
        });
    }

    [Fact]
    public void OffscreenSavedPlacementIsIgnored()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var originalBounds = GetBounds(window);

            App.ApplySavedWindowPlacement(
                window,
                new WindowPlacement(2500, 100, 400),
                CreateService(new WindowPlacementScreenBounds(0, 0, 1920, 1080)));

            Assert.Equal(originalBounds, GetBounds(window));
        });
    }

    [Fact]
    public void MissingSavedPlacementLeavesDefaultStartupBoundsUnchanged()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var originalBounds = GetBounds(window);

            App.ApplySavedWindowPlacement(
                window,
                null,
                CreateService(new WindowPlacementScreenBounds(0, 0, 1920, 1080)));

            Assert.Equal(originalBounds, GetBounds(window));
        });
    }

    private static (double Left, double Top, double Width, double Height) GetBounds(MainWindow window) =>
        (window.Left, window.Top, window.Width, window.Height);

    private static WindowPlacementService CreateService(params WindowPlacementScreenBounds[] screens) =>
        new(new FakeScreenProvider(screens));

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caughtException)
            {
                exception = caughtException;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The STA placement test did not complete within the timeout.");
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private sealed class FakeScreenProvider(IReadOnlyList<WindowPlacementScreenBounds> screens)
        : IWindowPlacementScreenProvider
    {
        public IReadOnlyList<WindowPlacementScreenBounds> GetScreenBounds() => screens;
    }
}
