using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Tests.Services;

[Collection("WPF")]
public sealed class MainWindowPlacementPersistenceTests
{
    [Fact]
    public void LocationAndWidthChangesPublishPlacementAfterDebounce()
    {
        RunOnSta(() =>
        {
            var placements = new List<WindowPlacement>();
            var window = CreateWindow(placements.Add);
            window.Show();
            PumpFor(TimeSpan.FromMilliseconds(100));
            placements.Clear();

            window.Left += 120;
            window.Top += 180;
            window.Width = window.Width > 400 ? 380 : 440;

            WaitUntil(() => placements.Count >= 1);
            var placement = Assert.Single(placements);
            Assert.Equal(window.Left, placement.Left, precision: 5);
            Assert.Equal(window.Top, placement.Top, precision: 5);
            Assert.Equal(window.Width, placement.Width, precision: 5);

            CloseWithoutShutdown(window);
        });
    }

    [Fact]
    public void RapidBoundsChangesPublishOnlyFinalPlacement()
    {
        RunOnSta(() =>
        {
            var placements = new List<WindowPlacement>();
            var window = CreateWindow(placements.Add);
            window.Show();
            PumpFor(TimeSpan.FromMilliseconds(100));
            placements.Clear();

            window.Left += 100;
            window.Left += 100;
            window.Top += 300;
            window.Width = window.Width > 400 ? 380 : 440;

            WaitUntil(() => placements.Count >= 1);
            var placement = placements[^1];
            Assert.Equal(window.Left, placement.Left, precision: 5);
            Assert.Equal(window.Top, placement.Top, precision: 5);
            Assert.Equal(window.Width, placement.Width, precision: 5);

            CloseWithoutShutdown(window);
        });
    }

    [Fact]
    public void ApplyPlacementDoesNotPublishPlacement()
    {
        RunOnSta(() =>
        {
            var placements = new List<WindowPlacement>();
            var window = CreateWindow(placements.Add);

            window.ApplyPlacement(new WindowPlacement(120, 180, 440));
            window.Show();
            PumpFor(TimeSpan.FromMilliseconds(100));

            Assert.Empty(placements);
            CloseWithoutShutdown(window);
        });
    }

    [Fact]
    public void HideDoesNotPublishPendingPlacement()
    {
        RunOnSta(() =>
        {
            var placements = new List<WindowPlacement>();
            var window = CreateWindow(placements.Add);
            window.Show();
            window.Left = 120;
            window.Hide();

            PumpFor(TimeSpan.FromMilliseconds(100));

            Assert.Empty(placements);
            window.RequestExit();
            window.Close();
        });
    }

    [Fact]
    public void ClosedPublishesLastPlacementWithoutWaitingForDebounce()
    {
        RunOnSta(() =>
        {
            var placements = new List<WindowPlacement>();
            var window = CreateWindow(placements.Add);
            window.Show();
            window.Left = 120;
            window.Top = 180;
            window.Width = 640;

            CloseWithoutShutdown(window);

            var placement = Assert.Single(placements);
            Assert.Equal(window.Left, placement.Left, precision: 5);
            Assert.Equal(window.Top, placement.Top, precision: 5);
            Assert.Equal(window.Width, placement.Width, precision: 5);
        });
    }

    [Fact]
    public void NonNormalWindowStateDoesNotPublishPlacement()
    {
        RunOnSta(() =>
        {
            var placements = new List<WindowPlacement>();
            var window = CreateWindow(placements.Add);
            window.Show();
            window.WindowState = WindowState.Minimized;
            window.Left = 120;

            PumpFor(TimeSpan.FromMilliseconds(100));

            Assert.Empty(placements);
            window.RequestExit();
            window.Close();
        });
    }

    [Fact]
    public void DynamicHeightChangeDoesNotPublishPlacement()
    {
        RunOnSta(() =>
        {
            var placements = new List<WindowPlacement>();
            var window = CreateWindow(placements.Add);
            window.Show();
            PumpFor(TimeSpan.FromMilliseconds(100));
            placements.Clear();

            window.Height += 100;

            PumpFor(TimeSpan.FromMilliseconds(100));

            Assert.Empty(placements);
            CloseWithoutShutdown(window);
        });
    }

    private static MainWindow CreateWindow(Action<WindowPlacement> placementChanged) =>
        new MainWindow(() => { }, placementChanged, TimeSpan.FromMilliseconds(20));

    private static void CloseWithoutShutdown(MainWindow window)
    {
        window.RequestExit();
        window.Close();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) =>
        {
            if (condition() || stopwatch.Elapsed >= TimeSpan.FromSeconds(2))
                frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();

        Assert.True(condition(), "The expected placement callback did not occur within the timeout.");
    }

    private static void PumpFor(TimeSpan duration)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) =>
        {
            if (stopwatch.Elapsed >= duration)
                frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

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

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The STA placement persistence test did not complete within the timeout.");
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
