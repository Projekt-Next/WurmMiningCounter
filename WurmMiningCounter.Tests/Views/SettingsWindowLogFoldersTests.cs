using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using WurmMiningCounter.Models;
using WurmMiningCounter.ViewModels;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Tests.Views;

[Collection("WPF")]
public sealed class SettingsWindowLogFoldersTests
{
    [Fact]
    public void RemoveFolderClickImmediatelyRemovesTheSelectedBoundItem()
    {
        RunOnSta(() =>
        {
            var viewModel = new SettingsViewModel(new AppSettings
            {
                AdditionalWurmDataLocations = [@"D:\\First", @"D:\\Second"]
            });
            LogLocationsManagerWindow? window = null;
            try
            {
                window = new LogLocationsManagerWindow(viewModel);
                window.Show();
                window.UpdateLayout();

                var folders = Assert.IsType<ListBox>(window.FindName("AdditionalFoldersList"));
                var remove = FindVisualChildren<Button>(window).Single(button => Equals(button.Content, "Remove"));
                folders.SelectedItem = @"D:\\First";
                Assert.False(remove.Focusable);

                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();

                Assert.Equal([@"D:\\Second"], viewModel.AdditionalWurmDataLocations);
                Assert.Equal(@"D:\\Second", Assert.Single(folders.Items.Cast<string>()));
                Assert.Null(folders.SelectedItem);
                Assert.False(remove.IsEnabled);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void RemoveFolderClickUsesTheViewModelSelectionInsteadOfTransientListBoxState()
    {
        RunOnSta(() =>
        {
            var viewModel = new SettingsViewModel(new AppSettings
            {
                AdditionalWurmDataLocations = [@"D:\\Only"]
            });
            LogLocationsManagerWindow? window = null;
            try
            {
                window = new LogLocationsManagerWindow(viewModel);
                window.Show();
                window.UpdateLayout();

                var folders = Assert.IsType<ListBox>(window.FindName("AdditionalFoldersList"));
                var remove = FindVisualChildren<Button>(window).Single(button => Equals(button.Content, "Remove"));
                folders.SelectedItem = @"D:\\Only";
                Assert.Equal(@"D:\\Only", viewModel.SelectedAdditionalWurmDataLocation);

                BindingOperations.ClearBinding(folders, Selector.SelectedItemProperty);
                folders.SelectedItem = null;
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Empty(viewModel.AdditionalWurmDataLocations);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void ManagerPresentsKnownLocationsAndRemainsResizable()
    {
        RunOnSta(() =>
        {
            var viewModel = new SettingsViewModel(new AppSettings());
            LogLocationsManagerWindow? window = null;
            try
            {
                window = new LogLocationsManagerWindow(viewModel);
                window.Show();
                window.UpdateLayout();

                Assert.InRange(window.Width, 799, 801);
                Assert.InRange(window.Height, 649, 651);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                window.Width = 680;
                window.Height = 520;
                window.UpdateLayout();
                Assert.InRange(window.Width, 679, 681);
                Assert.InRange(window.Height, 519, 521);
                Assert.Equal(3, viewModel.KnownLogLocations.Count);
                Assert.Equal("×", FindVisualChildren<Button>(window).Single(button => button.Content?.ToString() == "×").Content);
                Assert.Equal(
                    ["Wurm Online Standard", "Wurm Online Steam", "Wurm Unlimited Steam"],
                    viewModel.KnownLogLocations.Select(location => location.DisplayName));
                Assert.DoesNotContain(FindVisualChildren<CheckBox>(window), checkBox => checkBox.Content?.ToString() == "Enabled");
                Assert.False(Assert.Single(FindVisualChildren<Expander>(window)).IsExpanded);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void SettingsGeneralOpensTheSharedLogLocationsManager()
    {
        RunOnSta(() =>
        {
            var viewModel = new SettingsViewModel(new AppSettings());
            SettingsWindow? settings = null;
            try
            {
                settings = new SettingsWindow(viewModel);
                settings.Show();
                settings.UpdateLayout();

                Assert.DoesNotContain(FindVisualChildren<TabItem>(settings), tab => Equals(tab.Header, "Log folders"));
                Assert.Contains(FindVisualChildren<Button>(settings), button => button.Content?.ToString() == "×");
                var manage = FindVisualChildren<Button>(settings)
                    .Single(button => Equals(button.Content, "Manage log locations"));
                manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var manager = Assert.IsType<LogLocationsManagerWindow>(Assert.Single(settings.OwnedWindows));
                Assert.Same(viewModel, manager.DataContext);
                manager.Close();
            }
            finally
            {
                settings?.Close();
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
            try { action(); }
            catch (Exception caught) { exception = caught; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The STA Settings window test did not complete.");
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
