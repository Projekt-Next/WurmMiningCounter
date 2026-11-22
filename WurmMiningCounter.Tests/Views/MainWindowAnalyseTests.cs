using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Tests.Views;

[Collection("WPF")]
public sealed class MainWindowAnalyseTests
{
    private static readonly PlayerProfileId Alice = PlayerProfileId.CreateNew();
    private static readonly PlayerProfileId Bob = PlayerProfileId.CreateNew();

    [Fact]
    public void AnalyseOnlyProfilesUpdatesResetAndWindowReuseFollowIdentity()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var vm = new MainViewModel(new MiningSessionCounter(), postToUi: action => Dispatcher.CurrentDispatcher.BeginInvoke(action), analyseProcessor: processor);
            var window = new MainWindow(() => { }) { DataContext = vm };
            try
            {
                window.Show();
                Line(processor, Alice, "Same", "You start to analyse the shard.");
                Pump();
                Assert.Empty(vm.ProfilePanels);
                Line(processor, Alice, "Same", "You cannot see anything unusual.");
                Line(processor, Alice, "Same", "You finish analysing the shard.");
                Pump(); window.UpdateLayout();
                Assert.True(Assert.Single(vm.ProfilePanels).HasAnalyseResult);
                var firstButton = ButtonFor(window, Alice);
                Assert.Equal(Visibility.Visible, firstButton.Visibility);
                firstButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var results = Assert.Single(window.OwnedWindows.OfType<AnalyseResultsWindow>());
                Assert.Equal("Shard", ((TextBlock)results.FindName("InputText")).Text);
                ButtonFor(window, Alice).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(results, Assert.Single(window.OwnedWindows.OfType<AnalyseResultsWindow>()));

                Line(processor, Bob, "Same", "You start to analyse the ore.");
                Line(processor, Bob, "Same", "You cannot see anything unusual.");
                Line(processor, Bob, "Same", "You finish analysing the ore.");
                Pump(); window.UpdateLayout();
                ButtonFor(window, Bob).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(results, Assert.Single(window.OwnedWindows.OfType<AnalyseResultsWindow>()));
                Assert.Equal("Ore", ((TextBlock)results.FindName("InputText")).Text);
                processor.Invalidate(Bob, "Same");
                Pump(); window.UpdateLayout();
                Assert.Single(vm.ProfilePanels);
                Assert.Equal(Alice, vm.ProfilePanels[0].ProfileId);
                Assert.Equal(Visibility.Visible, ((TextBlock)results.FindName("EmptyText")).Visibility);
                results.Close();
                ButtonFor(window, Alice).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotSame(results, Assert.Single(window.OwnedWindows.OfType<AnalyseResultsWindow>()));
            }
            finally
            {
                foreach (Window owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
                window.Close();
                vm.DetachAnalyseUpdates();
            }
        });
    }

    [Theory]
    [InlineData(185)]
    [InlineData(200)]
    [InlineData(280)]
    public void ButtonFitsSkillRowWithoutIncreasingHeight(double width)
    {
        RunOnSta(() =>
        {
            var counter = new MiningSessionCounter();
            counter.Add(Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
            var processor = new AnalyseProcessor();
            var vm = new MainViewModel(counter, postToUi: action => Dispatcher.CurrentDispatcher.BeginInvoke(action), analyseProcessor: processor);
            typeof(MainViewModel).GetMethod("LoadLiveSkillSnapshots", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm,
                [new[] { new LiveSkillProfileSnapshot(Alice, "Alice", null, LiveSkillSourceState.Observing,
                    [new SkillStateSnapshot("Mining", 99.123456m, null, 12.123456m)]) }]);
            var window = new MainWindow(() => { }) { DataContext = vm, Width = width };
            try
            {
                window.Show(); window.UpdateLayout();
                var hidden = ButtonFor(window, Alice);
                Assert.Equal(Visibility.Collapsed, hidden.Visibility);
                var heightBefore = window.ActualHeight;
                Line(processor, Alice, "Alice", "You start to analyse the shard.");
                Line(processor, Alice, "Alice", "You cannot see anything unusual.");
                Line(processor, Alice, "Alice", "You finish analysing the shard.");
                Pump(); window.UpdateLayout();
                var button = ButtonFor(window, Alice);
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.InRange(Math.Abs(window.ActualHeight - heightBefore), 0, 0.1);
                var skill = Descendants<TextBlock>(window).Single(x => x.Text == "99.123456");
                var gain = Descendants<TextBlock>(window).Single(x => x.Text == "+12.123456");
                var buttonLeft = button.TransformToAncestor(window).Transform(new Point()).X;
                var skillRight = skill.TransformToAncestor(window).Transform(new Point()).X + skill.ActualWidth;
                var gainRight = gain.TransformToAncestor(window).Transform(new Point()).X + gain.ActualWidth;
                Assert.True(skillRight <= buttonLeft && gainRight <= buttonLeft);
                Assert.True(buttonLeft + button.ActualWidth <= window.ActualWidth);
                Assert.Equal("99.123456", skill.ToolTip);
                Assert.Equal("+12.123456", gain.ToolTip);
                Assert.True(skill.ActualWidth >= TextWidth(skill, width == 280 ? skill.Text : "99…"));
                Assert.True(gain.ActualWidth >= TextWidth(gain, width == 280 ? gain.Text : "+1…"));
                processor.Invalidate(Alice, "Alice");
                Pump(); window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, ButtonFor(window, Alice).Visibility);
                Assert.Equal(0, ButtonFor(window, Alice).ActualWidth);
            }
            finally { window.Close(); vm.DetachAnalyseUpdates(); }
        });
    }

    [Fact]
    public void ClosingMainWindowClosesAnalyseWindowAndDetachesItsSubscription()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var vm = new MainViewModel(new MiningSessionCounter(), postToUi: action => Dispatcher.CurrentDispatcher.BeginInvoke(action), analyseProcessor: processor);
            Line(processor, Alice, "Alice", "You start to analyse the shard.");
            Line(processor, Alice, "Alice", "You cannot see anything unusual.");
            Line(processor, Alice, "Alice", "You finish analysing the shard.");
            Pump();
            var window = new MainWindow(() => { }) { DataContext = vm };
            window.Show(); window.UpdateLayout();
            ButtonFor(window, Alice).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(window.OwnedWindows.OfType<AnalyseResultsWindow>());
            vm.DetachAnalyseUpdates();
            window.Close();
            Assert.Null(typeof(AnalyseProcessor).GetField("AnalyseChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor));
        });
    }

    private static Button ButtonFor(Window window, PlayerProfileId id) => Descendants<Button>(window)
        .Single(button => Equals(button.ToolTip, "Show Analyse results") && button.DataContext is ProfilePanelViewModel panel && panel.ProfileId == id);

    private static double TextWidth(TextBlock text, string value) => new FormattedText(value,
        System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
        Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip).Width;

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Line(AnalyseProcessor processor, PlayerProfileId id, string name, string text) =>
        processor.Process(new OrderedEventLine(id, name, 1, new EventLogLine(id, name, "event.txt", text)));

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
