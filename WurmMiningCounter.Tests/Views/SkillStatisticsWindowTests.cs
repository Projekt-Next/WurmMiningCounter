using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.ViewModels;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Tests.Views;

[Collection("WPF")]
public sealed class SkillStatisticsWindowTests
{
    [Fact]
    public void ProfileSelection_RemainsStableAcrossSnapshotUpdatesAndUsesProfileId()
    {
        RunOnSta(() =>
        {
            using var monitor = new LiveSkillMonitor(Path.GetTempPath(), TimeSpan.FromDays(1));
            var viewModel = new SkillStatisticsViewModel(monitor, action => action());
            var alpha = Snapshot("Alpha", "11111111-1111-1111-1111-111111111111", 80m);
            var beta = Snapshot("Beta", "22222222-2222-2222-2222-222222222222", 70m);
            var betaUpdated = Snapshot("Beta", "22222222-2222-2222-2222-222222222222", 70.001m);
            var sameFirst = Snapshot("Same", "33333333-3333-3333-3333-333333333333", 60m);
            var sameSecond = Snapshot("Same", "44444444-4444-4444-4444-444444444444", 50m);
            SkillStatisticsWindow? window = null;
            try
            {
                ApplySnapshots(viewModel, alpha, beta, sameFirst, sameSecond);
                window = new SkillStatisticsWindow(viewModel);
                window.Show();
                window.UpdateLayout();
                var profiles = Assert.Single(FindVisualChildren<ComboBox>(window));

                profiles.SelectedValue = beta.ProfileId;
                window.UpdateLayout();
                Assert.Equal(beta.ProfileId, viewModel.SelectedProfileId);

                ApplySnapshots(viewModel, Snapshot("Alpha", "11111111-1111-1111-1111-111111111111", 80.001m), beta);
                AssertSelection(beta.ProfileId, viewModel, profiles);

                ApplySnapshots(viewModel, alpha, betaUpdated);
                AssertSelection(beta.ProfileId, viewModel, profiles);

                ApplySnapshots(viewModel, alpha, betaUpdated, sameFirst, sameSecond);
                AssertSelection(beta.ProfileId, viewModel, profiles);
                Assert.Equal(2, viewModel.Profiles.Count(profile => profile.ProfileName == "Same"));
                Assert.Contains(viewModel.Profiles, profile => profile.ProfileId == sameFirst.ProfileId);
                Assert.Contains(viewModel.Profiles, profile => profile.ProfileId == sameSecond.ProfileId);

                // The displayed names are intentionally identical. Selection
                // must follow the actual TwoWay SelectedValue identity, not the
                // visible text or current list order.
                profiles.SelectedValue = sameFirst.ProfileId;
                window.UpdateLayout();
                AssertSelection(sameFirst.ProfileId, viewModel, profiles);

                var sameFirstUpdated = Snapshot("Same", "33333333-3333-3333-3333-333333333333", 60.001m);
                var sameSecondUpdated = Snapshot("Same", "44444444-4444-4444-4444-444444444444", 50.001m);
                ApplySnapshots(viewModel, sameSecondUpdated, betaUpdated, alpha, sameFirstUpdated);
                AssertSelection(sameFirst.ProfileId, viewModel, profiles);
                Assert.Equal(2, viewModel.Profiles.Count(profile => profile.ProfileName == "Same"));

                profiles.SelectedValue = sameSecond.ProfileId;
                window.UpdateLayout();
                AssertSelection(sameSecond.ProfileId, viewModel, profiles);

                ApplySnapshots(viewModel, alpha, sameFirstUpdated, sameSecondUpdated, betaUpdated);
                AssertSelection(sameSecond.ProfileId, viewModel, profiles);

                profiles.SelectedValue = alpha.ProfileId;
                window.UpdateLayout();
                AssertSelection(alpha.ProfileId, viewModel, profiles);

                ApplySnapshots(viewModel, betaUpdated, sameFirst, sameSecond);
                AssertSelection(beta.ProfileId, viewModel, profiles);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void OtherSkillsTableHasSortableNumericColumnsAndNoManualSessionControls()
    {
        RunOnSta(() =>
        {
            using var monitor = new LiveSkillMonitor(Path.GetTempPath(), TimeSpan.FromDays(1));
            var viewModel = new SkillStatisticsViewModel(monitor, action => action());
            SkillStatisticsWindow? window = null;
            try
            {
                window = new SkillStatisticsWindow(viewModel);
                window.Show();
                window.UpdateLayout();

                var table = Assert.Single(FindVisualChildren<DataGrid>(window));
                Assert.True(table.CanUserSortColumns);
                Assert.Equal(["Skill", "Current", "Last Gain", "Session Gain"], table.Columns.Select(column => column.Header));
                Assert.Equal("CurrentValue", table.Columns[1].SortMemberPath);
                Assert.Equal("LastGain", table.Columns[2].SortMemberPath);
                Assert.Equal("SessionGain", table.Columns[3].SortMemberPath);
                Assert.Equal(typeof(decimal), typeof(OtherSkillStatisticsRow).GetProperty(nameof(OtherSkillStatisticsRow.CurrentValue))!.PropertyType);
                Assert.Equal(typeof(decimal?), typeof(OtherSkillStatisticsRow).GetProperty(nameof(OtherSkillStatisticsRow.LastGain))!.PropertyType);
                var buttonContents = FindVisualChildren<Button>(window)
                    .Select(button => button.Content?.ToString())
                    .ToArray();
                Assert.DoesNotContain("Start Session", buttonContents);
                Assert.DoesNotContain("Stop Session", buttonContents);
                Assert.DoesNotContain("Reset Session", buttonContents);
                Assert.DoesNotContain("New Session", buttonContents);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void ProfileComboBoxGroupsPhysicalInstallationsAndKeepsProfileIdSelectionStable()
    {
        RunOnSta(() =>
        {
            using var monitor = new LiveSkillMonitor(Path.GetTempPath(), TimeSpan.FromDays(1));
            var viewModel = new SkillStatisticsViewModel(monitor, action => action());
            var standard = Snapshot("Same", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 10m,
                @"C:\WurmStandard", "Wurm Online Standard");
            var steam = Snapshot("Same", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", 20m,
                @"D:\WurmSteam", "Wurm Online Steam");
            SkillStatisticsWindow? window = null;
            try
            {
                ApplySnapshots(viewModel, standard, steam);
                window = new SkillStatisticsWindow(viewModel);
                window.Show();
                window.UpdateLayout();
                var profiles = Assert.Single(FindVisualChildren<ComboBox>(window));

                profiles.SelectedValue = steam.ProfileId;
                profiles.IsDropDownOpen = true;
                window.UpdateLayout();

                Assert.Equal(steam.ProfileId, viewModel.SelectedProfileId);
                Assert.Equal("Same - Wurm Online Steam", Assert.IsType<SkillStatisticsProfileOption>(profiles.SelectedItem).DisplayText);
                var groupedView = CollectionViewSource.GetDefaultView(profiles.ItemsSource);
                Assert.Equal(2, groupedView.Groups!.Count);
                Assert.NotNull(Assert.Single(profiles.GroupStyle).HeaderTemplate);
                Assert.Equal(2, profiles.Items.Count);
                var item = Assert.IsType<ComboBoxItem>(profiles.ItemContainerGenerator.ContainerFromItem(profiles.Items[0]));
                Assert.Contains(FindVisualChildren<TextBlock>(item), text => text.Text == "Same");
                Assert.DoesNotContain(FindVisualChildren<TextBlock>(item), text => text.Text?.Contains("Wurm Online", StringComparison.Ordinal) == true);

                profiles.SelectedValue = standard.ProfileId;
                window.UpdateLayout();
                AssertSelection(standard.ProfileId, viewModel, profiles);

                ApplySnapshots(viewModel,
                    Snapshot("Same", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", 20.1m, @"D:\WurmSteam", "Wurm Online Steam"),
                    Snapshot("Same", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 10.1m, @"C:\WurmStandard", "Wurm Online Standard"));
                AssertSelection(standard.ProfileId, viewModel, profiles);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void ProfileComboBoxUsesPixelVirtualizedScrollingForLongGroupedList()
    {
        RunOnSta(() =>
        {
            using var monitor = new LiveSkillMonitor(Path.GetTempPath(), TimeSpan.FromDays(1));
            var viewModel = new SkillStatisticsViewModel(monitor, action => action());
            var snapshots = Enumerable.Range(0, 16).Select(index => Snapshot($"Profile {index}",
                $"00000000-0000-0000-0000-{index + 1:000000000000}", index,
                index < 8 ? @"C:\Standard" : @"D:\Steam", index < 8 ? "Wurm Online Standard" : "Wurm Online Steam")).ToArray();
            SkillStatisticsWindow? window = null;
            try
            {
                ApplySnapshots(viewModel, snapshots);
                window = new SkillStatisticsWindow(viewModel);
                window.Show();
                window.UpdateLayout();
                var profiles = Assert.Single(FindVisualChildren<ComboBox>(window));
                profiles.IsDropDownOpen = true;
                window.UpdateLayout();

                var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(profiles.Template.FindName("PART_Popup", profiles));
                var scrollViewer = Assert.Single(FindVisualChildren<ScrollViewer>(Assert.IsAssignableFrom<DependencyObject>(popup.Child)));
                Assert.True(scrollViewer.ScrollableHeight > 20);
                var panel = Assert.IsType<VirtualizingStackPanel>(profiles.ItemsPanel.LoadContent());
                Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(panel));
                scrollViewer.ScrollToVerticalOffset(17);
                window.UpdateLayout();
                Assert.InRange(scrollViewer.VerticalOffset, 16.5, 17.5);
                scrollViewer.ScrollToVerticalOffset(34);
                window.UpdateLayout();
                Assert.InRange(scrollViewer.VerticalOffset, 33.5, 34.5);
                scrollViewer.ScrollToVerticalOffset(17);
                window.UpdateLayout();
                Assert.InRange(scrollViewer.VerticalOffset, 16.5, 17.5);
            }
            finally
            {
                window?.Close();
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

    private static LiveSkillProfileSnapshot Snapshot(string profileName, string id, decimal miningCurrent,
        string? presentationRootPath = null, string presentationGroupName = "Unknown installation") => new(
        new PlayerProfileId(Guid.Parse(id)), profileName, null, LiveSkillSourceState.Observing,
        [new SkillStateSnapshot("Mining", miningCurrent, 0.001m, miningCurrent - decimal.Truncate(miningCurrent), 1)])
    {
        ObservationStartedAtUtc = DateTimeOffset.UnixEpoch,
        PresentationRootPath = presentationRootPath,
        PresentationGroupName = presentationGroupName
    };

    private static void ApplySnapshots(SkillStatisticsViewModel viewModel, params LiveSkillProfileSnapshot[] snapshots) =>
        typeof(SkillStatisticsViewModel).GetMethod("ApplySnapshots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [snapshots]);

    private static void AssertSelection(PlayerProfileId expected, SkillStatisticsViewModel viewModel, ComboBox profiles)
    {
        Assert.Equal(expected, viewModel.SelectedProfileId);
        Assert.Equal(expected, profiles.SelectedValue);
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception caught) { exception = caught; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The STA Skill Statistics window test did not complete.");
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
