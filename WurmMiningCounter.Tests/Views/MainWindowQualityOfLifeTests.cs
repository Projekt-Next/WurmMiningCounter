using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.Views;

[Collection("WPF")]
public sealed class MainWindowQualityOfLifeTests
{
    [Fact]
    public void MinimizeButtonMinimizesTheWindow()
    {
        RunOnSta(() =>
        {
            var window = new MainWindow(() => { });
            try
            {
                window.Show();
                var button = FindVisualChildren<Button>(window)
                    .Single(button => Equals(button.ToolTip, "Minimize"));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(WindowState.Minimized, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ProspectingResultsContextMenuRequiresStateAndConfirmationAndUsesProfileReset()
    {
        RunOnSta(() =>
        {
            var counter = new MiningSessionCounter();
            counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
            counter.Add(EventTransportTestProfileIds.Bob, "Bob", new MiningEvent(MiningMaterial.Diamond));
            var resetProfiles = new List<PlayerProfileId>();
            var viewModel = new MainViewModel(
                counter,
                initialVeinContexts:
                [
                    new VeinContextSnapshot(
                        EventTransportTestProfileIds.Alice, "Alice", VeinMaterial.Iron,
                        new QualityMeasurement.ExactMax(12.5m),
                        new RemainingMeasurement.Exact(1103),
                        VeinConfidence.Synchronized)
                ],
                resetVeinContextAsync: profileId =>
                {
                    resetProfiles.Add(profileId);
                    return Task.CompletedTask;
                });
            var window = new MainWindow(() => { }) { DataContext = viewModel };
            try
            {
                window.Show();
                window.UpdateLayout();
                var headings = FindVisualChildren<TextBlock>(window)
                    .Where(text => text.Text == "Prospecting Results")
                    .ToArray();
                var aliceHeading = headings.Single(text =>
                    text.DataContext is ProfilePanelViewModel { ProfileName: "Alice" });
                var bobHeading = headings.Single(text =>
                    text.DataContext is ProfilePanelViewModel { ProfileName: "Bob" });
                Assert.True(ContextMenuService.GetIsEnabled(aliceHeading));
                Assert.False(ContextMenuService.GetIsEnabled(bobHeading));
                Assert.NotNull(aliceHeading.ContextMenu);
                Assert.Equal(1, aliceHeading.ContextMenu.Padding.Left);
                Assert.Equal(29, Assert.IsType<MenuItem>(Assert.Single(aliceHeading.ContextMenu.Items)).Height);
                Assert.Equal("Reset Prospecting Results", ((MenuItem)aliceHeading.ContextMenu.Items[0]).Header);
                Assert.DoesNotContain(FindVisualChildren<Button>(window),
                    button => Equals(button.ToolTip, "Reset Prospecting Results"));

                var alice = viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Alice");
                var cancelled = window.ConfirmAndResetProspectingResultsAsync(alice.ProfileId, _ => false);
                Assert.True(cancelled.IsCompletedSuccessfully);
                Assert.Empty(resetProfiles);

                var confirmed = window.ConfirmAndResetProspectingResultsAsync(alice.ProfileId, message =>
                {
                    Assert.Contains("Alice", message);
                    return true;
                });
                Assert.True(confirmed.IsCompletedSuccessfully);
                Assert.Equal([EventTransportTestProfileIds.Alice], resetProfiles);

                var bob = viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Bob");
                var unavailable = window.ConfirmAndResetProspectingResultsAsync(bob.ProfileId, _ =>
                    throw new InvalidOperationException("Confirmation should not open without a context."));
                Assert.True(unavailable.IsCompletedSuccessfully);
                Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
                Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].Materials[MiningMaterial.Ore]);
                Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Bob].Materials[MiningMaterial.Diamond]);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ActiveProspectingConfidenceUsesTheRequestedColors()
    {
        RunOnSta(() =>
        {
            var viewModel = new MainViewModel(
                new MiningSessionCounter(),
                initialVeinContexts:
                [
                    new VeinContextSnapshot(
                        EventTransportTestProfileIds.Alice, "Alice", VeinMaterial.Iron,
                        new QualityMeasurement.ExactMax(12.5m),
                        new RemainingMeasurement.Exact(1103),
                        VeinConfidence.Synchronized),
                    new VeinContextSnapshot(
                        EventTransportTestProfileIds.Bob, "Bob", VeinMaterial.Zinc,
                        new QualityMeasurement.ExactMax(12.5m),
                        new RemainingMeasurement.Exact(1102),
                        VeinConfidence.Tracking)
                ]);
            var window = new MainWindow(() => { }) { DataContext = viewModel };
            try
            {
                window.Show();
                window.UpdateLayout();
                var confidenceTexts = FindVisualChildren<TextBlock>(window)
                    .Where(text => text.DataContext is ProfileVeinContextViewModel context
                        && text.Text == context.ConfidenceText)
                    .ToArray();

                Assert.Equal(2, confidenceTexts.Length);
                Assert.Equal(Color.FromRgb(0x15, 0x65, 0xC0),
                    ((SolidColorBrush)confidenceTexts.Single(text =>
                        ((ProfileVeinContextViewModel)text.DataContext).Confidence == VeinConfidence.Synchronized).Foreground).Color);
                Assert.Equal(Color.FromRgb(0x2E, 0x7D, 0x32),
                    ((SolidColorBrush)confidenceTexts.Single(text =>
                        ((ProfileVeinContextViewModel)text.DataContext).Confidence == VeinConfidence.Tracking).Foreground).Color);
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The STA quality of life test did not complete in time.");
        if (exception is not null)
            throw new InvalidOperationException("The STA quality of life test failed.", exception);
    }
}
