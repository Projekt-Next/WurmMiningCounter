using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.Views;

[Collection("WPF")]
public sealed class MainWindowMaterialResetTests
{
    [Fact]
    public void MaterialMenuKeepsP2LogicalContextAfterP1RebuildAndClose()
    {
        RunOnSta(() =>
        {
            var counter = new MiningSessionCounter();
            counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
            counter.Add(EventTransportTestProfileIds.Bob, "Bob", new MiningEvent(MiningMaterial.Flint));
            var viewModel = new MainViewModel(counter);
            var window = new MainWindow(() => { }) { DataContext = viewModel };
            try
            {
                window.Show();
                window.UpdateLayout();
                var host = FindVisualChildren<Border>(window).Single(border =>
                    border.ContextMenu is not null && border.DataContext is MiningMaterialCountViewModel { Material: MiningMaterial.Flint });
                var menu = host.ContextMenu!;
                menu.PlacementTarget = host;
                InvokePrivate(window, "OnMaterialContextMenuOpened", menu, new RoutedEventArgs());
                counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
                InvokePrivate(window, "OnResetContextMenuClosed", menu, new RoutedEventArgs());

                var context = GetMenuContext(window, menu);
                Assert.NotNull(context);
                Assert.Equal(EventTransportTestProfileIds.Bob, context!.GetType().GetProperty("ProfileId")!.GetValue(context));
                Assert.Equal(MiningMaterial.Flint, context.GetType().GetProperty("Material")!.GetValue(context));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void MaterialMenuReopenReplacesItsPreviousLogicalContext()
    {
        RunOnSta(() =>
        {
            var (window, aliceHost, bobHost) = CreateWindowWithAliceOreAndBobFlint();
            try
            {
                var menu = aliceHost.ContextMenu!;
                menu.PlacementTarget = aliceHost;
                InvokePrivate(window, "OnMaterialContextMenuOpened", menu, new RoutedEventArgs());
                var initialContext = GetMenuContext(window, menu);
                Assert.NotNull(initialContext);
                Assert.Equal(EventTransportTestProfileIds.Alice, initialContext!.GetType().GetProperty("ProfileId")!.GetValue(initialContext));

                menu.PlacementTarget = bobHost;
                InvokePrivate(window, "OnMaterialContextMenuOpened", menu, new RoutedEventArgs());

                var context = GetMenuContext(window, menu);
                Assert.NotNull(context);
                Assert.Equal(EventTransportTestProfileIds.Bob, context!.GetType().GetProperty("ProfileId")!.GetValue(context));
                Assert.Equal(MiningMaterial.Flint, context.GetType().GetProperty("Material")!.GetValue(context));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void MaterialMenuFailedReopenDoesNotRetainPreviousLogicalContext()
    {
        RunOnSta(() =>
        {
            var (window, aliceHost, _) = CreateWindowWithAliceOreAndBobFlint();
            try
            {
                var menu = aliceHost.ContextMenu!;
                menu.PlacementTarget = aliceHost;
                InvokePrivate(window, "OnMaterialContextMenuOpened", menu, new RoutedEventArgs());
                Assert.NotNull(GetMenuContext(window, menu));

                menu.PlacementTarget = new Border();
                InvokePrivate(window, "OnMaterialContextMenuOpened", menu, new RoutedEventArgs());

                Assert.Null(GetMenuContext(window, menu));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DifferentMaterialMenusKeepIndependentLogicalContexts()
    {
        RunOnSta(() =>
        {
            var (window, aliceHost, bobHost) = CreateWindowWithAliceOreAndBobFlint();
            try
            {
                var aliceMenu = aliceHost.ContextMenu!;
                var bobMenu = bobHost.ContextMenu!;
                aliceMenu.PlacementTarget = aliceHost;
                bobMenu.PlacementTarget = bobHost;
                InvokePrivate(window, "OnMaterialContextMenuOpened", aliceMenu, new RoutedEventArgs());
                InvokePrivate(window, "OnMaterialContextMenuOpened", bobMenu, new RoutedEventArgs());

                var aliceContext = GetMenuContext(window, aliceMenu);
                var bobContext = GetMenuContext(window, bobMenu);
                Assert.NotNull(aliceContext);
                Assert.NotNull(bobContext);
                Assert.Equal(EventTransportTestProfileIds.Alice, aliceContext!.GetType().GetProperty("ProfileId")!.GetValue(aliceContext));
                Assert.Equal(EventTransportTestProfileIds.Bob, bobContext!.GetType().GetProperty("ProfileId")!.GetValue(bobContext));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ResetMenuContextsUseWeakKeysAndDoNotStoreVisualState()
    {
        var contextField = typeof(MainWindow).GetField("_resetMenuContexts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(typeof(ConditionalWeakTable<,>), contextField.FieldType.GetGenericTypeDefinition());
        Assert.Equal(typeof(ContextMenu), contextField.FieldType.GetGenericArguments()[0]);

        var contextType = typeof(MainWindow).GetNestedType("ResetMenuContext", System.Reflection.BindingFlags.NonPublic)!;
        Assert.All(contextType.GetProperties(), property =>
            Assert.DoesNotContain(typeof(DependencyObject), property.PropertyType.GetInterfaces()
                .Append(property.PropertyType)));
    }

    [Fact]
    public void MaterialRowsExposeResetMenuAndConfirmationDelegatesToSharedReset()
    {
        RunOnSta(() =>
        {
            var counter = new MiningSessionCounter();
            counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
            counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Flint));
            var viewModel = new MainViewModel(counter);
            var window = new MainWindow(() => { }) { DataContext = viewModel };
            try
            {
                window.Show();
                window.UpdateLayout();

                var materialRows = FindVisualChildren<Grid>(window)
                    .Where(grid => grid.DataContext is MiningMaterialCountViewModel)
                    .ToArray();
                Assert.Equal(2, materialRows.Length);
                var oreRow = materialRows.Single(row =>
                    ((MiningMaterialCountViewModel)row.DataContext).Material == MiningMaterial.Ore);
                Assert.Null(oreRow.ContextMenu);
                var oreNameHost = FindVisualChildren<Border>(oreRow)
                    .Single(border => border.ContextMenu is not null);
                var oreCount = FindVisualChildren<TextBlock>(oreRow)
                    .Single(textBlock => textBlock.Text == "1");
                Assert.Null(oreCount.ContextMenu);
                Assert.Equal(48, oreNameHost.MinWidth);
                var flintRow = materialRows.Single(row =>
                    ((MiningMaterialCountViewModel)row.DataContext).Material == MiningMaterial.Flint);
                var flintNameHost = FindVisualChildren<Border>(flintRow)
                    .Single(border => border.ContextMenu is not null);
                Assert.Equal(0, flintNameHost.MinWidth);

                oreNameHost.ContextMenu!.PlacementTarget = oreNameHost;
                MainWindow.ConfigureMaterialContextMenu(oreNameHost.ContextMenu);
                Assert.NotNull(oreNameHost.ContextMenu.Template);
                Assert.Equal(System.Windows.Media.Colors.White, ((SolidColorBrush)oreNameHost.ContextMenu.Background).Color);
                var menuItem = Assert.IsType<MenuItem>(Assert.Single(oreNameHost.ContextMenu!.Items));
                Assert.Equal("Reset Ore counter", menuItem.Header);
                Assert.Equal(29, menuItem.Height);
                Assert.Equal(12, menuItem.FontSize);
                Assert.NotNull(menuItem.Style);
                Assert.Contains(
                    menuItem.Style.Setters.OfType<Setter>(),
                    setter => setter.Property == MenuItem.TemplateProperty);

                var profile = Assert.Single(viewModel.ProfilePanels);
                var ore = profile.Materials.Single(material => material.Material == MiningMaterial.Ore);
                var cancelled = window.ConfirmAndResetMaterialCounter(profile, ore, _ => false);

                Assert.False(cancelled);
                Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].Materials[MiningMaterial.Ore]);

                var confirmation = MainWindow.CreateMaterialResetConfirmation(ore);
                Assert.Contains("Reset Ore counter?", confirmation);
                Assert.Contains("Current value: 1", confirmation);
                Assert.Contains("Total and other counters will not be affected.", confirmation);

                var reset = window.ConfirmAndResetMaterialCounter(profile, ore, _ => true);

                Assert.True(reset);
                var snapshot = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
                Assert.Equal(1, snapshot.TotalCount);
                Assert.DoesNotContain(MiningMaterial.Ore, snapshot.Materials.Keys);
                Assert.Equal(1, snapshot.Materials[MiningMaterial.Flint]);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MiningInspirationMenuKeepsP2LogicalContextAfterP1RebuildAndClose()
    {
        RunOnSta(() =>
        {
            var (window, _, counter, _, _, _, bobHost) = CreateWindowWithMiningInspirations();
            try
            {
                var menu = bobHost.ContextMenu!;
                menu.PlacementTarget = bobHost;
                InvokePrivate(window, "OnMiningInspirationContextMenuOpened", menu, new RoutedEventArgs());
                Assert.Equal("Reset Moment of Inspiration", Assert.IsType<MenuItem>(Assert.Single(menu.Items)).Header);
                counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
                InvokePrivate(window, "OnResetContextMenuClosed", menu, new RoutedEventArgs());

                var context = GetMenuContext(window, menu);
                Assert.NotNull(context);
                Assert.Equal(EventTransportTestProfileIds.Bob, context!.GetType().GetProperty("ProfileId")!.GetValue(context));
                Assert.Equal("MiningInspiration", context.GetType().GetProperty("Operation")!.GetValue(context)!.ToString());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void MiningInspirationMenuReopenReplacesAndFailedOpenClearsLogicalContext()
    {
        RunOnSta(() =>
        {
            var (window, _, _, _, _, aliceHost, bobHost) = CreateWindowWithMiningInspirations();
            try
            {
                var menu = aliceHost.ContextMenu!;
                menu.PlacementTarget = aliceHost;
                InvokePrivate(window, "OnMiningInspirationContextMenuOpened", menu, new RoutedEventArgs());
                Assert.Equal(EventTransportTestProfileIds.Alice, GetMenuContext(window, menu)!.GetType().GetProperty("ProfileId")!.GetValue(GetMenuContext(window, menu)!));

                menu.PlacementTarget = bobHost;
                InvokePrivate(window, "OnMiningInspirationContextMenuOpened", menu, new RoutedEventArgs());
                Assert.Equal(EventTransportTestProfileIds.Bob, GetMenuContext(window, menu)!.GetType().GetProperty("ProfileId")!.GetValue(GetMenuContext(window, menu)!));

                menu.PlacementTarget = new Border();
                InvokePrivate(window, "OnMiningInspirationContextMenuOpened", menu, new RoutedEventArgs());
                Assert.Null(GetMenuContext(window, menu));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task MiningInspirationConfirmationNoDoesNotResetAndYesResetsExactlyOnce()
    {
        await RunOnStaAsync(async () =>
        {
            var (window, _, _, _, resetProfiles, _, _) = CreateWindowWithMiningInspirations();
            try
            {
                var profile = Assert.Single(((MainViewModel)window.DataContext!).ProfilePanels,
                    panel => panel.ProfileName == "Alice");

                Assert.False(await window.ConfirmAndResetMiningInspirationAsync(profile.ProfileId, _ => false));
                Assert.Empty(resetProfiles);
                Assert.Equal(1, profile.MiningInspirationCount);

                Assert.True(await window.ConfirmAndResetMiningInspirationAsync(profile.ProfileId, _ => true));
                Assert.Equal([EventTransportTestProfileIds.Alice], resetProfiles);
                Assert.Equal(0, ((MainViewModel)window.DataContext!).ProfilePanels
                    .Single(panel => panel.ProfileName == "Alice").MiningInspirationCount);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task MiningInspirationConfirmationRejectsProfileRemovedAfterMenuOpen()
    {
        await RunOnStaAsync(async () =>
        {
            var (window, viewModel, counter, inspirations, _, _, bobHost) = CreateWindowWithMiningInspirations();
            try
            {
                var menu = bobHost.ContextMenu!;
                menu.PlacementTarget = bobHost;
                InvokePrivate(window, "OnMiningInspirationContextMenuOpened", menu, new RoutedEventArgs());
                var context = GetMenuContext(window, menu)!;
                var profileId = Assert.IsType<PlayerProfileId>(context.GetType().GetProperty("ProfileId")!.GetValue(context));

                inspirations.ResetProfile(EventTransportTestProfileIds.Bob);
                counter.ResetProfile(EventTransportTestProfileIds.Bob);
                viewModel.Refresh();

                Assert.False(await window.ConfirmAndResetMiningInspirationAsync(profileId, _ => true));
                Assert.DoesNotContain(viewModel.ProfilePanels, panel => panel.ProfileName == "Bob");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ProspectingAndNearbyMenusKeepP2LogicalContextAfterP1Rebuild()
    {
        await RunOnStaAsync(async () =>
        {
            var counter = new MiningSessionCounter();
            counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
            counter.Add(EventTransportTestProfileIds.Bob, "Bob", new MiningEvent(MiningMaterial.Flint));
            var vein = new VeinContextProcessor();
            var nearby = new NearbyScanProcessor();
            var resetProfiles = new List<PlayerProfileId>();
            var viewModel = new MainViewModel(
                counter,
                veinContextProcessor: vein,
                nearbyScanProcessor: nearby,
                postToUi: action => action(),
                resetVeinContextAsync: profileId =>
                {
                    resetProfiles.Add(profileId);
                    return Task.CompletedTask;
                });
            ProcessLine(vein, "Bob", 1, "You start to gather fragments of the rock.");
            ProcessLine(vein, "Bob", 2, "You would mine stone shards here.");
            ProcessLine(nearby, "Bob", 1, "There is iron nearby.");
            var window = new MainWindow(() => { }) { DataContext = viewModel };
            try
            {
                window.Show();
                window.UpdateLayout();
                var bobProspecting = FindVisualChildren<TextBlock>(window).Single(textBlock =>
                    textBlock.Text == "Prospecting Results"
                    && textBlock.DataContext is ProfilePanelViewModel { ProfileName: "Bob" });
                var bobNearby = FindVisualChildren<TextBlock>(window).Single(textBlock =>
                    textBlock.Text == "Nearby Scan"
                    && textBlock.DataContext is ProfilePanelViewModel { ProfileName: "Bob" });
                Assert.Equal("Reset Prospecting Results", Assert.IsType<MenuItem>(
                    Assert.Single(bobProspecting.ContextMenu!.Items)).Header);
                Assert.Equal("Reset Prospecting Results", Assert.IsType<MenuItem>(
                    Assert.Single(bobNearby.ContextMenu!.Items)).Header);

                var menu = bobNearby.ContextMenu!;
                menu.PlacementTarget = bobNearby;
                InvokePrivate(window, "OnProspectingResultsContextMenuOpened", menu, new RoutedEventArgs());
                counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Shards));
                InvokePrivate(window, "OnResetContextMenuClosed", menu, new RoutedEventArgs());

                var context = GetMenuContext(window, menu);
                Assert.NotNull(context);
                Assert.Equal(EventTransportTestProfileIds.Bob, context.GetType().GetProperty("ProfileId")!.GetValue(context));
                Assert.Equal("ProspectingResults", context.GetType().GetProperty("Operation")!.GetValue(context)!.ToString());
                Assert.True(await window.ConfirmAndResetProspectingResultsAsync(EventTransportTestProfileIds.Bob, _ => true));
                Assert.Equal([EventTransportTestProfileIds.Bob], resetProfiles);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typedChild)
                yield return typedChild;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static object? GetMenuContext(MainWindow window, ContextMenu menu)
    {
        var field = typeof(MainWindow).GetField("_resetMenuContexts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var table = field.GetValue(window)!;
        var arguments = new object?[] { menu, null };
        var found = Assert.IsType<bool>(table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments));
        return found ? arguments[1] : null;
    }

    private static (MainWindow Window, Border AliceHost, Border BobHost) CreateWindowWithAliceOreAndBobFlint()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", new MiningEvent(MiningMaterial.Flint));
        var window = new MainWindow(() => { }) { DataContext = new MainViewModel(counter) };
        window.Show();
        window.UpdateLayout();
        var hosts = FindVisualChildren<Border>(window)
            .Where(border => border.ContextMenu is not null)
            .ToArray();
        return (
            window,
            hosts.Single(host => host.DataContext is MiningMaterialCountViewModel { Material: MiningMaterial.Ore }),
            hosts.Single(host => host.DataContext is MiningMaterialCountViewModel { Material: MiningMaterial.Flint }));
    }

    private static (
        MainWindow Window,
        MainViewModel ViewModel,
        MiningSessionCounter Counter,
        MiningInspirationCounter Inspirations,
        List<PlayerProfileId> ResetProfiles,
        TextBlock AliceHost,
        TextBlock BobHost) CreateWindowWithMiningInspirations(bool show = true)
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", new MiningEvent(MiningMaterial.Shards));
        var inspirations = new MiningInspirationCounter();
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Ore);
        inspirations.Add(EventTransportTestProfileIds.Bob, "Bob", MiningMaterial.Shards);
        var resetProfiles = new List<PlayerProfileId>();
        var viewModel = new MainViewModel(
            counter,
            miningInspirationCounter: inspirations,
            resetMiningInspirationAsync: profileId =>
            {
                resetProfiles.Add(profileId);
                inspirations.ResetProfile(profileId);
                return Task.CompletedTask;
            });
        var window = new MainWindow(() => { }) { DataContext = viewModel };
        if (show)
        {
            window.Show();
            window.UpdateLayout();
        }

        var hosts = FindVisualChildren<TextBlock>(window)
            .Where(textBlock => textBlock.ContextMenu is not null
                && textBlock.DataContext is ProfilePanelViewModel
                && textBlock.Text == "Moment of Inspiration")
            .ToArray();
        return (
            window,
            viewModel,
            counter,
            inspirations,
            resetProfiles,
            hosts.Single(host => ((ProfilePanelViewModel)host.DataContext).ProfileName == "Alice"),
            hosts.Single(host => ((ProfilePanelViewModel)host.DataContext).ProfileName == "Bob"));
    }

    private static void InvokePrivate(MainWindow window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, arguments);

    private static void ProcessLine(
        VeinContextProcessor processor,
        string profileName,
        long sequenceNumber,
        string text) =>
        processor.Process(new OrderedEventLine(
            EventTransportTestProfileIds.For(profileName),
            profileName,
            sequenceNumber,
            new EventLogLine(EventTransportTestProfileIds.For(profileName), profileName, "Event.txt", text)));

    private static void ProcessLine(
        NearbyScanProcessor processor,
        string profileName,
        long sequenceNumber,
        string text) =>
        processor.Process(new OrderedEventLine(
            EventTransportTestProfileIds.For(profileName),
            profileName,
            sequenceNumber,
            new EventLogLine(EventTransportTestProfileIds.For(profileName), profileName, "Event.txt", text)));

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

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The STA material reset test did not complete in time.");
        if (exception is not null)
            throw new InvalidOperationException("The STA material reset test failed.", exception);
    }

    private static async Task RunOnStaAsync(Func<Task> action)
    {
        Exception? exception = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(async () =>
        {
            try { await action(); }
            catch (Exception caughtException) { exception = caughtException; }
            finally { completion.TrySetResult(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (exception is not null)
            throw new InvalidOperationException("The STA mining inspiration reset test failed.", exception);
    }
}
