using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Tests.Views;

[Collection("WPF")]
public sealed class AnalyseResultsWindowTests
{
    [Theory]
    [InlineData("a trace", "Trace")]
    [InlineData("a slight trace", "Slight trace")]
    [InlineData("a faint trace", "Faint trace")]
    [InlineData("a minuscule trace", "Minuscule trace")]
    [InlineData("a vague trace", "Vague trace")]
    [InlineData("an indistinct trace", "Indistinct trace")]
    public void EveryTraceStrengthUsesShortSeparator(string strength, string label)
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            Publish(processor, id, "Alice", Shard, $"You see {strength} of acceptable quality sandstone.", ShardFinish);
            var window = new AnalyseResultsWindow(processor, id, "Alice");
            try
            {
                window.Show(); window.UpdateLayout();
                var block = Assert.Single(Descendants<TextBlock>((ItemsControl)window.FindName("EntriesList")));
                var text = new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text;
                Assert.Equal($"{label} - sandstone\nQuality: acceptable", text);
                AssertSingleAsciiSeparator(text);
                var runs = block.Inlines.OfType<System.Windows.Documents.Run>().ToArray();
                Assert.Equal(FontWeights.Bold, runs[1].FontWeight);
                Assert.Equal(FontWeights.Normal, runs[0].FontWeight);
                Assert.Equal(FontWeights.Normal, runs[2].FontWeight);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TracePresentationEmphasizesOnlyMaterialAndOmitsStrengthFromCards()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            Publish(processor, id, "Alice", Shard, Trace,
                "You see a slight trace of salt.", Marker, ShardFinish);
            var window = new AnalyseResultsWindow(processor, id, "Alice");
            try
            {
                window.Show(); window.UpdateLayout();
                var cards = (ItemsControl)window.FindName("VeinsList");
                var cardText = Descendants<TextBlock>(cards).Select(text => text.Text).ToArray();
                Assert.Contains("slate", cardText);
                Assert.Contains("salt", cardText);
                Assert.Contains("1 tiles", cardText);
                Assert.Contains("2 tiles", cardText);
                Assert.Contains("Quality: utmost\nDirection: east", cardText);
                Assert.DoesNotContain(cardText, text => text.Contains("trace", StringComparison.OrdinalIgnoreCase));

                var list = (ItemsControl)window.FindName("EntriesList");
                var blocks = Descendants<TextBlock>(list).ToArray();
                Assert.Equal(3, blocks.Length);
                var first = blocks[0].Inlines.OfType<System.Windows.Documents.Run>().ToArray();
                Assert.Equal(new[] { "Trace - ", "slate", "\nQuality: utmost\nDirection: east" }, first.Select(run => run.Text));
                var firstText = new System.Windows.Documents.TextRange(blocks[0].ContentStart, blocks[0].ContentEnd).Text;
                Assert.Equal("Trace - slate\nQuality: utmost\nDirection: east", firstText);
                AssertSingleAsciiSeparator(firstText);
                Assert.Equal(FontWeights.Normal, first[0].FontWeight);
                Assert.Equal(FontWeights.Bold, first[1].FontWeight);
                Assert.Equal(FontWeights.Normal, first[2].FontWeight);
                var second = blocks[1].Inlines.OfType<System.Windows.Documents.Run>().ToArray();
                Assert.Equal(new[] { "Slight trace - ", "salt", "" }, second.Select(run => run.Text));
                var secondText = new System.Windows.Documents.TextRange(blocks[1].ContentStart, blocks[1].ContentEnd).Text;
                Assert.Equal("Slight trace - salt", secondText);
                AssertSingleAsciiSeparator(secondText);
                Assert.Equal(FontWeights.Bold, second[1].FontWeight);
                Assert.Equal(Marker, Entries(window)[2]);
            }
            finally { window.Close(); }
        });
    }

    private static readonly string[] LiveNoCompass =
    [
        "[08:26:53] You start to analyse the shard.",
        "[08:26:53] Lots of different sized stone shards.",
        "[08:26:55] You spot a trace of very good quality copper.",
        "[08:26:55] You take a closer look.",
        "[08:26:58] You see a slight trace of salt.",
        "[08:26:58] You see a slight trace of normal quality iron.",
        "[08:26:58] You notice a slight trace of poor quality iron.",
        "[08:26:58] You study it a bit more.",
        "[08:27:02] You cannot see anything unusual.",
        "[08:27:02] You study it real hard.",
        "[08:27:05] You see a minuscule trace of acceptable quality tin.",
        "[08:27:05] You see a minuscule trace of salt.",
        "[08:27:05] You finish analysing the shard."
    ];

    [Fact]
    public void LiveNoCompassSequencePreservesObservationsAndDisplaysUnknownPositions()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            Publish(processor, id, "Alice", LiveNoCompass);
            var result = Assert.Single(processor.GetSnapshots()).Result;
            Assert.Equal(AnalyseInput.Shard, result.Input);
            Assert.Equal("08:27:05", result.FinishTime);
            Assert.Equal(LiveNoCompass.Skip(2).Take(10), result.Entries.Select(entry => entry.RawText));
            var traces = result.Entries.Select(entry => entry.Trace).OfType<AnalyseTrace>().ToArray();
            Assert.Equal(6, traces.Length);
            Assert.All(traces, trace => Assert.Null(trace.Direction));
            Assert.Equal(new[] { "normal", "poor" }, traces.Where(t => t.Material == "iron").Select(t => t.Quality));
            Assert.Equal(2, traces.Count(t => t.Material == "salt" && t.Quality is null));
            Assert.Single(result.Entries.Where(entry => entry.NoTrace is not null));
            Assert.Equal(3, result.Entries.Count(entry => entry.StageMarker is not null));
            var window = new AnalyseResultsWindow(processor, id, "Alice");
            try
            {
                window.Show();
                Assert.Equal(6, ((ItemsControl)window.FindName("VeinsList")).Items.Count);
                Assert.Equal(10, Entries(window).Length);
                Assert.Equal("Ostatnia analiza: 08:27:05", Text(window, "FinishTimeText"));
                Assert.Contains("nieznane", Text(window, "MapNoteText"));
                Assert.Equal(225, ((Grid)window.FindName("MapGrid")).Children.Count);
                Publish(processor, id, "Alice", Shard, Trace, LiveNoCompass[2], ShardFinish);
                Pump();
                Assert.Equal(2, ((ItemsControl)window.FindName("VeinsList")).Items.Count);
                Assert.Equal(226, ((Grid)window.FindName("MapGrid")).Children.Count);
                Assert.Contains("nieznane", Text(window, "MapNoteText"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void FinishTimeFollowsSelectedSnapshotAndReset()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var first = PlayerProfileId.CreateNew();
            var second = PlayerProfileId.CreateNew();
            Publish(processor, first, "Same", Shard, NoTrace, "[01:13:10] You finish analysing the shard.");
            Publish(processor, second, "Same", Ore, NoTrace, "[22:03:04] You finish analysing the ore.");
            var window = new AnalyseResultsWindow(processor, first, "Same");
            try
            {
                window.Show();
                Assert.Equal("Ostatnia analiza: 01:13:10", Text(window, "FinishTimeText"));
                window.SelectProfile(second, "Same");
                Assert.Equal("Ostatnia analiza: 22:03:04", Text(window, "FinishTimeText"));
                Publish(processor, second, "Same", Shard, Trace);
                Pump();
                Assert.Equal("Ostatnia analiza: 22:03:04", Text(window, "FinishTimeText"));
                processor.Invalidate(second, "Same"); Pump();
                Assert.Equal("Ostatnia analiza: czas niedostępny", Text(window, "FinishTimeText"));
                window.SelectProfile(first, "Same");
                Assert.Equal("Ostatnia analiza: 01:13:10", Text(window, "FinishTimeText"));
                Publish(processor, first, "Same", Shard, NoTrace, ShardFinish); Pump();
                Assert.Equal("Ostatnia analiza: czas niedostępny", Text(window, "FinishTimeText"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DefaultSizePersistsAcrossProfileSwitchAndReopening()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var first = PlayerProfileId.CreateNew();
            var second = PlayerProfileId.CreateNew();
            Publish(processor, first, "Alice", new[] { Shard }.Concat(Enumerable.Repeat(Trace, 30)).Append(ShardFinish).ToArray());
            Publish(processor, second, "Bob", Ore, NoTrace, OreFinish);
            for (var i = 0; i < 2; i++)
            {
                var window = new AnalyseResultsWindow(processor, first, "Alice");
                try
                {
                    window.Show(); window.UpdateLayout();
                    Assert.Equal(600, window.Width);
                    Assert.InRange(window.Height, 686.5, 687.5);
                    Assert.Equal(window.MinWidth, window.Width);
                    Assert.Equal(604, window.MinHeight);
                    var map = (Grid)window.FindName("MapGrid");
                    var mapBounds = map.TransformToAncestor(window).TransformBounds(new Rect(0, 0, map.ActualWidth, map.ActualHeight));
                    Assert.InRange(mapBounds.Width, 149.5, 151);
                    Assert.True(((ScrollViewer)window.FindName("VeinsScroll")).ScrollableHeight > 0);
                    var entries = (ScrollViewer)window.FindName("EntriesScroll");
                    Assert.True(entries.ActualHeight >= 100);
                    Assert.True(entries.ScrollableHeight > 0);
                    window.SelectProfile(second, "Bob"); window.UpdateLayout();
                    Assert.Equal(600, window.ActualWidth);
                    Assert.InRange(window.ActualHeight, 686.5, 687.5);
                    var switchedBounds = map.TransformToAncestor(window).TransformBounds(new Rect(0, 0, map.ActualWidth, map.ActualHeight));
                    Assert.InRange(switchedBounds.Width, 149.5, 300);
                }
                finally { window.Close(); }
            }
        });
    }

    [Theory]
    [InlineData(604)]
    [InlineData(687)]
    public void WindowRendersMapLegendAndScrollableSections(int height)
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            Publish(processor, id, "Alice", new[] { Shard }.Concat(Enumerable.Repeat(Trace, 30)).Append(ShardFinish).ToArray());
            var window = new AnalyseResultsWindow(processor, id, "Alice") { Width = 600, Height = height };
            try
            {
                window.Show(); window.UpdateLayout();
                Assert.InRange(window.ActualWidth, 599.5, 600.5);
                Assert.InRange(window.ActualHeight, height - 0.5, height + 0.5);
                var map = (Grid)window.FindName("MapGrid");
                var bounds = map.TransformToAncestor(window).TransformBounds(new Rect(0, 0, map.ActualWidth, map.ActualHeight));
                Assert.True(bounds.Width >= 100 && bounds.Height >= 100, $"Map bounds: {bounds}");
                var legend = (TextBlock)window.FindName("LegendText");
                Assert.True(legend.IsVisible && legend.ActualHeight > 0);
                foreach (var name in new[] { "VeinsScroll", "EntriesScroll" })
                {
                    var scroll = (ScrollViewer)window.FindName(name);
                    Assert.True(scroll.IsVisible && scroll.ViewportHeight > 0 && scroll.ScrollableHeight > 0, $"{name}: viewport {scroll.ViewportHeight}, scrollable {scroll.ScrollableHeight}");
                    scroll.ScrollToBottom(); window.UpdateLayout();
                    Assert.True(scroll.VerticalOffset > 0);
                }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(600, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(734)]
    [InlineData(960)]
    public void BothListsScrollWithLocalWmcStyle(double width)
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            Publish(processor, id, "Alice", new[] { Shard }.Concat(Enumerable.Repeat(Trace, 30)).Append(ShardFinish).ToArray());
            var window = new AnalyseResultsWindow(processor, id, "Alice") { Width = width };
            try
            {
                window.Show(); window.UpdateLayout();
                foreach (var name in new[] { "VeinsScroll", "EntriesScroll" })
                {
                    var scroll = (ScrollViewer)window.FindName(name);
                    Assert.True(scroll.ScrollableHeight > 0);
                    var bar = Descendants<System.Windows.Controls.Primitives.ScrollBar>(scroll).Single(item => item.Orientation == System.Windows.Controls.Orientation.Vertical);
                    Assert.Equal(8, bar.Width);
                    var section = (GroupBox)window.FindName(name == "VeinsScroll" ? "VeinsSection" : "EntriesSection");
                    var right = bar.TransformToAncestor(section).Transform(new Point(bar.ActualWidth, 0)).X;
                    Assert.InRange(section.ActualWidth - right, 6, 8);
                    scroll.ScrollToBottom(); window.UpdateLayout();
                    Assert.True(scroll.VerticalOffset > 0);
                }
                Assert.Equal(12, ((TextBlock)window.FindName("LegendText")).FontSize);
                var entries = (ItemsControl)window.FindName("EntriesList");
                Assert.All(Descendants<TextBlock>(entries), item => Assert.Equal(13, item.FontSize));
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    [Theory]
    [InlineData("west of south", -2, 5)]
    [InlineData("south of west", -5, 2)]
    [InlineData("east of north", 2, -5)]
    [InlineData("north of east", 5, -2)]
    public void IntermediateDirectionsKeepTheirCorrectOrientation(string direction, int x, int y)
    {
        var method = typeof(AnalyseResultsWindow).GetMethod("MapPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal((x, y), ((int X, int Y))method.Invoke(null, [direction, 5])!);
        foreach (var radius in Enumerable.Range(1, 6))
        {
            var position = ((int X, int Y))method.Invoke(null, [direction, radius])!;
            Assert.Equal(radius, Math.Max(Math.Abs(position.X), Math.Abs(position.Y)));
        }
    }

    [Fact]
    public void MapShowsSixthRingAndAllDirectionsInsideCells()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            string[] directions = ["north", "east of north", "northeast", "north of east", "east", "south of east", "southeast", "east of south", "south", "west of south", "southwest", "south of west", "west", "north of west", "northwest", "west of north"];
            var lines = new[] { Shard }.Concat(directions.Select(direction => $"You notice an indistinct trace of something, but cannot quite make it out ({direction})."))
                .Append(ShardFinish).ToArray();
            Publish(processor, id, "Alice", lines);
            var window = new AnalyseResultsWindow(processor, id, "Alice");
            try
            {
                window.Show(); window.UpdateLayout();
                var grid = (Grid)window.FindName("MapGrid");
                Assert.Equal(15, grid.RowDefinitions.Count);
                Assert.Equal(15, grid.ColumnDefinitions.Count);
                var cells = grid.Children.OfType<Border>().ToArray();
                var markers = cells.Where(cell => cell.ToolTip is string).ToArray();
                Assert.Equal(16, markers.Length);
                Assert.All(markers, marker =>
                {
                    Assert.Equal(6, Math.Max(Math.Abs(Grid.GetRow(marker) - 7), Math.Abs(Grid.GetColumn(marker) - 7)));
                    Assert.True(marker.Margin.Left > 0 && marker.Margin.Top > 0 && marker.Margin.Right > 0 && marker.Margin.Bottom > 0);
                });
                Assert.Single(cells.Where(cell => Grid.GetRow(cell) == 7 && Grid.GetColumn(cell) == 7));
                Assert.Contains("6 Indistinct", Text(window, "LegendText"));
                Assert.Equal(16, ((ItemsControl)window.FindName("VeinsList")).Items.Count);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void RepeatedObservationsKeepMatchingColorsAndRingsAcrossRefresh()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            Publish(processor, id, "Alice", Shard, Trace, Trace, Unknown, ShardFinish);
            var window = new AnalyseResultsWindow(processor, id, "Alice");
            try
            {
                window.Show(); window.UpdateLayout();
                var list = (ItemsControl)window.FindName("VeinsList");
                var colors = list.Items.Cast<object>().Select(item => item.GetType().GetProperty("Color")!.GetValue(item)).ToArray();
                Assert.Equal(3, colors.Distinct().Count());
                var grid = (Grid)window.FindName("MapGrid");
                var markers = grid.Children.OfType<Border>().Where(cell => cell.ToolTip is string).ToArray();
                Assert.Equal(2, markers.Length);
                var grouped = Assert.Single(markers.Where(marker => Math.Max(Math.Abs(Grid.GetRow(marker) - 7), Math.Abs(Grid.GetColumn(marker) - 7)) == 1));
                var marks = (System.Windows.Controls.Primitives.UniformGrid)grouped.Child;
                Assert.Equal(2, marks.Children.Count);
                Assert.Equal(colors[0], ((Border)marks.Children[0]).Background);
                Assert.Equal(colors[1], ((Border)marks.Children[1]).Background);
                window.SelectProfile(id, "Alice");
                Assert.Equal(colors.Select(color => color!.ToString()), list.Items.Cast<object>().Select(item => item.GetType().GetProperty("Color")!.GetValue(item)!.ToString()));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ProfileSelectorUsesIdentityForDuplicateNames()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var first = PlayerProfileId.CreateNew();
            var second = PlayerProfileId.CreateNew();
            Publish(processor, first, "Same", Shard, Trace, ShardFinish);
            Publish(processor, second, "Same", Ore, NoTrace, OreFinish);
            var window = new AnalyseResultsWindow(processor, first, "Same");
            try
            {
                window.Show();
                var selector = (ComboBox)window.FindName("ProfileSelector");
                Assert.Equal(2, selector.Items.Count);
                selector.SelectedIndex = selector.SelectedIndex == 0 ? 1 : 0;
                Assert.Equal("Ore", Text(window, "InputText"));
                Assert.Equal(NoTrace, Assert.Single(Entries(window)));
            }
            finally { window.Close(); }
        });
    }

    private const string Shard = "You start to analyse the shard.";
    private const string ShardFinish = "You finish analysing the shard.";
    private const string Ore = "You start to analyse the ore.";
    private const string OreFinish = "You finish analysing the ore.";
    private const string Trace = "You notice a trace of utmost quality slate (east).";
    private const string Unknown = "You spot an indistinct trace of something, but cannot quite make it out (south of east).";
    private const string NoTrace = "You cannot see anything unusual.";
    private const string Marker = "You take a closer look.";

    [Fact]
    public void PresentsOrderedResultsIncludingDuplicatesUnknownAndNoTrace()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            Publish(processor, id, "Alice", Shard, Trace, Trace, Marker, Unknown, NoTrace, ShardFinish);
            var window = new AnalyseResultsWindow(processor, id, "Alice");
            try
            {
                window.Show();
                Assert.Equal("Shard", Text(window, "InputText"));
                Assert.Equal("Alice", Text(window, "ProfileText"));
                var entries = Entries(window);
                Assert.Equal(5, entries.Length);
                Assert.Equal(entries[0], entries[1]);
                Assert.Contains("Trace - slate", entries[0]);
                Assert.Contains("Quality: utmost", entries[0]);
                Assert.Contains("Direction: east", entries[0]);
                Assert.Equal(Marker, entries[2]);
                Assert.Contains("Indistinct trace - Unidentified material", entries[3]);
                Assert.DoesNotContain("Quality:", entries[3]);
                Assert.Equal(NoTrace, entries[4]);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void UpdatesResetSwitchAndReleaseFollowSelectedProfile()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var first = PlayerProfileId.CreateNew();
            var second = PlayerProfileId.CreateNew();
            Publish(processor, first, "Same", Shard, Trace, ShardFinish);
            Publish(processor, second, "Same", Ore, NoTrace, OreFinish);
            var window = new AnalyseResultsWindow(processor, first, "Same");
            try
            {
                window.Show();
                RunOnWorker(() => Publish(processor, second, "Same", Shard, Trace, ShardFinish));
                Pump();
                Assert.Equal("Shard", Text(window, "InputText"));
                Assert.Contains("slate", Assert.Single(Entries(window)));

                RunOnWorker(() => Publish(processor, first, "Same", Ore, NoTrace, OreFinish));
                Pump();
                Assert.Equal("Ore", Text(window, "InputText"));
                Assert.Equal(NoTrace, Assert.Single(Entries(window)));
                processor.ReleaseProfile(first);
                Assert.Equal(NoTrace, Assert.Single(Entries(window)));

                RunOnWorker(() => processor.Invalidate(first, "Same"));
                window.SelectProfile(second, "Same");
                Pump();
                Assert.Equal("Shard", Text(window, "InputText"));
                Assert.Contains("slate", Assert.Single(Entries(window)));
                processor.Invalidate(second, "Same");
                Pump();
                Assert.Empty(Entries(window));
                Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("EmptyText")).Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ClosingUnsubscribesAndQueuedUpdatesDoNotChangeWindow()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            Publish(processor, id, "Alice", Shard, Trace, ShardFinish);
            var window = new AnalyseResultsWindow(processor, id, "Alice");
            window.Show();
            RunOnWorker(() => Publish(processor, id, "Alice", Ore, NoTrace, OreFinish));
            window.Close();
            Pump();
            Assert.Equal("Shard", Text(window, "InputText"));
            Assert.Null(typeof(AnalyseProcessor).GetField("AnalyseChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(processor));
        });
    }

    [Fact]
    public void ContextFifoPublishesToOpenWindowAndQueuedResetWins()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            using var context = new EventLineCoordinator();
            context.RegisterConsumer(processor);
            using var published = new ManualResetEventSlim();
            processor.AnalyseChanged += (_, e) => { if (e.Snapshot is not null) published.Set(); };
            var window = new AnalyseResultsWindow(processor, id, "Alice");
            try
            {
                window.Show();
                context.AcceptOutput(new EventReadBatch(id, "Alice", "event.txt", 1,
                    [Shard, Trace, Trace, Marker, NoTrace, ShardFinish]));
                Assert.True(published.Wait(TimeSpan.FromSeconds(5)));
                Pump();
                Assert.Equal("Shard", Text(window, "InputText"));
                Assert.Equal(4, Entries(window).Length);

                published.Reset();
                context.AcceptOutput(new EventReadBatch(id, "Alice", "event.txt", 1, [Ore, NoTrace, OreFinish]));
                Assert.True(published.Wait(TimeSpan.FromSeconds(5)));
                // Complete the ordered reset before dispatching the queued UI update.
                RunOnWorker(() => WaitForReset(context, id));
                Pump();
                Assert.Empty(Entries(window));
                Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("EmptyText")).Visibility);
            }
            finally
            {
                window.Close();
                RunOnWorker(() => WaitForCompletion(context));
            }
        });
    }

    private static void WaitForReset(EventLineCoordinator context, PlayerProfileId id) =>
        context.ResetProfileAsync(id).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    private static void WaitForCompletion(EventLineCoordinator context) =>
        context.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    [Fact]
    public void LongResultScrollsInCompactWindow()
    {
        RunOnSta(() =>
        {
            var processor = new AnalyseProcessor();
            var id = PlayerProfileId.CreateNew();
            Publish(processor, id, "Alice", new[] { Shard }.Concat(Enumerable.Repeat(Trace, 30)).Append(ShardFinish).ToArray());
            var window = new AnalyseResultsWindow(processor, id, "Alice");
            try
            {
                window.Show();
                window.UpdateLayout();
                var scroll = (ScrollViewer)window.FindName("EntriesScroll");
                Assert.True(scroll.ScrollableHeight > 0);
                Assert.Equal(600, window.Width);
                Assert.Equal(30, Entries(window).Length);
            }
            finally { window.Close(); }
        });
    }

    private static ScrollViewer FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            try { return FindScroll(child); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("ScrollViewer not found.");
    }

    private static void Publish(AnalyseProcessor processor, PlayerProfileId id, string name, params string[] lines)
    {
        foreach (var text in lines)
            processor.Process(new OrderedEventLine(id, name, 1, new EventLogLine(id, name, "event.txt", text)));
    }

    private static string Text(Window window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static string[] Entries(Window window) => ((ItemsControl)window.FindName("EntriesList")).Items.Cast<object>().Select(item => item.ToString()!).ToArray();

    private static void AssertSingleAsciiSeparator(string text)
    {
        Assert.Equal(1, text.Count(character => character == '-'));
        Assert.Contains(" - ", text);
        Assert.DoesNotContain("—", text);
        Assert.DoesNotContain("–", text);
        Assert.DoesNotContain("--", text);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnWorker(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
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
