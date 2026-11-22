using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Tests.Views;

[Collection("WPF")]
public sealed class ResetWindowTests
{
    [Fact]
    public void MaterialControlsUseTheExistingSelectionViewModelBindings()
    {
        RunOnSta(() =>
        {
            ResetWindow? window = null;
            try
            {
                var counter = new MiningSessionCounter();
                counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
                counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Flint));
                var selection = new ResetSelectionViewModel(new MainViewModel(counter));
                var option = Assert.Single(selection.ProfileOptions);
                window = new ResetWindow(selection);
                window.Show();
                window.UpdateLayout();

                var ore = FindVisualChildren<CheckBox>(window).Single(checkBox => Equals(checkBox.Content, "Ore"));
                var reset = FindVisualChildren<Button>(window)
                    .Single(button => Equals(button.Content, "Reset"));
                var selectAll = FindVisualChildren<Button>(window)
                    .Single(button => Equals(button.Content, "Select All"));
                var resetAll = FindVisualChildren<CheckBox>(window)
                    .Single(checkBox => Equals(checkBox.Content, "Reset All Material Counters"));

                Assert.DoesNotContain(
                    FindVisualChildren<Button>(window),
                    button => Equals(button.Content, "Reset Selected"));
                Assert.False(reset.IsEnabled);
                Assert.DoesNotContain(FindVisualChildren<CheckBox>(window), checkBox => Equals(checkBox.Content, "Salt"));

                ore.IsChecked = true;
                Assert.True(option.MaterialOptions.Single(material => material.Material == MiningMaterial.Ore).IsSelected);
                Assert.True(reset.IsEnabled);

                selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.All(option.MaterialOptions, material => Assert.True(material.IsSelected));
                Assert.Equal("Clear All", selectAll.Content);

                selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.All(option.MaterialOptions, material => Assert.False(material.IsSelected));
                Assert.Equal("Select All", selectAll.Content);

                selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                resetAll.IsChecked = true;
                Assert.All(option.MaterialOptions, material => Assert.False(material.IsSelected));

                ore.IsChecked = true;
                Assert.False(option.ResetMaterialCounters);
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The STA reset window test did not complete in time.");
        if (exception is not null)
            throw new InvalidOperationException("The STA reset window test failed.", exception);
    }
}
