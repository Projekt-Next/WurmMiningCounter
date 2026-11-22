using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Tests.Views;

[Collection("WPF")]
public sealed class AlertsWindowTests
{
    [Fact]
    public void SidePanelAddCancelAndCustomEditUseTheSameWindow()
    {
        RunOnSta(() =>
        {
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), Custom()] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(new EventAlertConfigurationController(settings, catalog, _ => true));

            Assert.False(window.IsEditorOpen);
            Invoke(window, "OnAddClick", new object(), new RoutedEventArgs());
            Assert.True(window.IsEditorOpen);
            Assert.Equal("Add trigger", window.EditorTitle);
            Assert.Equal(string.Empty, window.Editor!.Name);

            Invoke(window, "OnCancelClick", new object(), new RoutedEventArgs());
            Assert.False(window.IsEditorOpen);
            Assert.Equal(3, catalog.GetSnapshot().Length);

            var customRow = window.AlertTriggers.Single(row => !row.Trigger.IsBuiltIn);
            var edit = new System.Windows.Controls.Button { Tag = customRow };
            Invoke(window, "OnEditClick", edit, new RoutedEventArgs());
            Assert.True(window.IsEditorOpen);
            Assert.Equal("Edit trigger", window.EditorTitle);
            Assert.Equal(customRow.Name, window.Editor!.Name);

            window.Editor.Name = "Edited";
            Invoke(window, "OnSaveEditorClick", new object(), new RoutedEventArgs());
            Assert.False(window.IsEditorOpen);
            Assert.Equal("Edited", catalog.GetSnapshot().Single(trigger => !trigger.IsBuiltIn).Name);
        });
    }

    [Fact]
    public void BuiltInsCanBeEditedButNotDeleted()
    {
        RunOnSta(() =>
        {
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
            var window = new AlertsWindow(new EventAlertConfigurationController(
                settings, new EventAlertCatalog(settings.EventAlerts), _ => true));

            var row = window.AlertTriggers[0];
            Assert.True(row.CanEdit);
            Assert.Equal(Visibility.Collapsed, row.DeleteVisibility);
            Assert.True(row.Enabled);
            Assert.True(row.SoundEnabled);
            Assert.True(row.PopupEnabled);
        });
    }

    [Fact]
    public void EditSelectsExistingCustomSoundAndSaveWithoutChangesPreservesIt()
    {
        RunOnSta(() =>
        {
            using var fixture = new SoundDirectoryFixture("custom.wav");
            var custom = Custom() with { SoundFileName = "custom.wav" };
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), custom] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(
                new EventAlertConfigurationController(settings, catalog, _ => true),
                new EventAlertSoundCatalog(fixture.DirectoryPath));
            window.Show();
            try
            {
                var row = window.AlertTriggers.Single(item => item.Trigger.Id == custom.Id);
                Invoke(window, "OnEditClick", new Button { Tag = row }, new RoutedEventArgs());
                window.UpdateLayout();

                Assert.Equal("custom.wav", window.Editor!.SoundFileName);
                Assert.Equal("custom.wav", Assert.Single(FindVisualChildren<ComboBox>(window)).SelectedItem);

                Invoke(window, "OnSaveEditorClick", new object(), new RoutedEventArgs());

                Assert.Equal("custom.wav", catalog.GetSnapshot().Single(item => item.Id == custom.Id).SoundFileName);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EditPreservesMissingCustomSoundFileName()
    {
        RunOnSta(() =>
        {
            using var fixture = new SoundDirectoryFixture();
            var custom = Custom() with { SoundFileName = "missing.wav" };
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), custom] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(
                new EventAlertConfigurationController(settings, catalog, _ => true),
                new EventAlertSoundCatalog(fixture.DirectoryPath));

            var row = window.AlertTriggers.Single(item => item.Trigger.Id == custom.Id);
            Invoke(window, "OnEditClick", new Button { Tag = row }, new RoutedEventArgs());

            Assert.Equal("missing.wav", window.Editor!.SoundFileName);
            Assert.Contains("missing.wav", window.Sounds);
            Invoke(window, "OnSaveEditorClick", new object(), new RoutedEventArgs());
            Assert.Equal("missing.wav", catalog.GetSnapshot().Single(item => item.Id == custom.Id).SoundFileName);
        });
    }

    [Fact]
    public void DeleteCustomDoesNothingWhenConfirmationIsCancelled()
    {
        RunOnSta(() =>
        {
            var custom = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), custom] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(
                new EventAlertConfigurationController(settings, catalog, _ => true),
                deleteConfirmation: _ => false);

            var row = window.AlertTriggers.Single(item => item.Trigger.Id == custom.Id);
            Invoke(window, "OnDeleteClick", new Button { Tag = row }, new RoutedEventArgs());

            Assert.Contains(catalog.GetSnapshot(), item => item.Id == custom.Id);
        });
    }

    [Fact]
    public void DeleteCustomRemovesItOnlyAfterConfirmation()
    {
        RunOnSta(() =>
        {
            var custom = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), custom] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(
                new EventAlertConfigurationController(settings, catalog, _ => true),
                deleteConfirmation: _ => true);

            var row = window.AlertTriggers.Single(item => item.Trigger.Id == custom.Id);
            Invoke(window, "OnDeleteClick", new Button { Tag = row }, new RoutedEventArgs());

            Assert.DoesNotContain(catalog.GetSnapshot(), item => item.Id == custom.Id);
        });
    }

    [Fact]
    public void ConfirmingDeleteOfEditedCustomTriggerClosesEditorAndUnlocksAllRows()
    {
        RunOnSta(() =>
        {
            var first = Custom();
            var second = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), first, second] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(
                new EventAlertConfigurationController(settings, catalog, _ => true),
                deleteConfirmation: _ => true);
            window.Show();
            try
            {
                Edit(window, first.Id);
                var editedRow = window.AlertTriggers.Single(row => row.Trigger.Id == first.Id);

                Invoke(window, "OnDeleteClick", new Button { Tag = editedRow }, new RoutedEventArgs());
                window.UpdateLayout();

                Assert.False(window.IsEditorOpen);
                Assert.DoesNotContain(catalog.GetSnapshot(), trigger => trigger.Id == first.Id);
                Assert.All(window.AlertTriggers, row => Assert.True(row.AreTogglesEnabled));
                Assert.All(TableToggles(window), toggle => Assert.True(toggle.IsEnabled));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CancellingDeleteOfEditedCustomTriggerKeepsEditorOpen()
    {
        RunOnSta(() =>
        {
            var custom = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), custom] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(
                new EventAlertConfigurationController(settings, catalog, _ => true),
                deleteConfirmation: _ => false);
            window.Show();
            try
            {
                Edit(window, custom.Id);
                var editedRow = window.AlertTriggers.Single(row => row.Trigger.Id == custom.Id);

                Invoke(window, "OnDeleteClick", new Button { Tag = editedRow }, new RoutedEventArgs());
                window.UpdateLayout();

                Assert.True(window.IsEditorOpen);
                Assert.Contains(catalog.GetSnapshot(), trigger => trigger.Id == custom.Id);
                Assert.All(TableToggles(window, custom.Id), toggle => Assert.False(toggle.IsEnabled));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DeletingAnotherCustomTriggerKeepsCurrentEditorOpen()
    {
        RunOnSta(() =>
        {
            var first = Custom();
            var second = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), first, second] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(
                new EventAlertConfigurationController(settings, catalog, _ => true),
                deleteConfirmation: _ => true);
            window.Show();
            try
            {
                Edit(window, first.Id);
                var otherRow = window.AlertTriggers.Single(row => row.Trigger.Id == second.Id);

                Invoke(window, "OnDeleteClick", new Button { Tag = otherRow }, new RoutedEventArgs());
                window.UpdateLayout();

                Assert.True(window.IsEditorOpen);
                Assert.Equal("Edit trigger", window.EditorTitle);
                Assert.DoesNotContain(catalog.GetSnapshot(), trigger => trigger.Id == second.Id);
                Assert.All(TableToggles(window, first.Id), toggle => Assert.False(toggle.IsEnabled));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BuiltInEditKeepsCanonicalTextAndAllowsSoundChange()
    {
        RunOnSta(() =>
        {
            using var fixture = new SoundDirectoryFixture("custom.wav");
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(
                new EventAlertConfigurationController(settings, catalog, _ => true),
                new EventAlertSoundCatalog(fixture.DirectoryPath));
            var row = window.AlertTriggers[0];

            Invoke(window, "OnEditClick", new Button { Tag = row }, new RoutedEventArgs());
            window.Editor!.Name = "Changed";
            window.Editor.MatchText = "Changed";
            window.Editor.PopupText = "Changed";
            window.Editor.SoundFileName = "custom.wav";
            Invoke(window, "OnSaveEditorClick", new object(), new RoutedEventArgs());

            var actual = catalog.GetSnapshot()[0];
            Assert.Equal(row.Name, actual.Name);
            Assert.Equal(row.MatchText, actual.MatchText);
            Assert.Equal(row.PopupText, actual.PopupText);
            Assert.Equal("custom.wav", actual.SoundFileName);
        });
    }

    [Fact]
    public void AddAndEditRefreshSoundFilesFromCatalog()
    {
        RunOnSta(() =>
        {
            var soundsDirectory = Path.Combine(Path.GetTempPath(), "WurmMiningCounter.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(soundsDirectory);
            try
            {
                File.WriteAllBytes(Path.Combine(soundsDirectory, "first.wav"), []);
                var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), Custom()] };
                var window = new AlertsWindow(
                    new EventAlertConfigurationController(settings, new EventAlertCatalog(settings.EventAlerts), _ => true),
                    new EventAlertSoundCatalog(soundsDirectory));

                Invoke(window, "OnAddClick", new object(), new RoutedEventArgs());
                Assert.Equal(["first.wav"], window.Sounds);

                File.WriteAllBytes(Path.Combine(soundsDirectory, "second.wav"), []);
                var customRow = window.AlertTriggers.Single(row => !row.Trigger.IsBuiltIn);
                Invoke(window, "OnEditClick", new Button { Tag = customRow }, new RoutedEventArgs());

                Assert.Equal(["first.wav", "second.wav", "wall-break-soon.wav"], window.Sounds);
            }
            finally
            {
                if (Directory.Exists(soundsDirectory))
                    Directory.Delete(soundsDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void FailedToggleSaveRestoresReadOnlyRowStateWithoutChangingRuntime()
    {
        RunOnSta(() =>
        {
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(new EventAlertConfigurationController(settings, catalog, _ => false));
            window.Show();
            try
            {
                Assert.True(window.IsLoaded);
                var row = window.AlertTriggers[0];
                var enabled = FindVisualChildren<CheckBox>(window)
                    .First(checkBox => ReferenceEquals(checkBox.Tag, row));

                enabled.IsChecked = false;

                Assert.True(catalog.GetSnapshot()[0].Enabled);
                Assert.True(window.AlertTriggers[0].Enabled);
                Assert.Equal("Settings save failed.", window.ErrorMessage);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EditingOneRowDisablesOnlyItsTableTogglesUntilCancelOrSaveSecondRegression()
    {
        RunOnSta(() =>
        {
            var custom = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), custom] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(new EventAlertConfigurationController(settings, catalog, _ => true));

            var customRow = window.AlertTriggers.Single(row => row.Trigger.Id == custom.Id);
            Invoke(window, "OnEditClick", new Button { Tag = customRow }, new RoutedEventArgs());
            Assert.False(window.AlertTriggers.Single(row => row.Trigger.Id == custom.Id).AreTogglesEnabled);
            Assert.All(window.AlertTriggers.Where(row => row.Trigger.Id != custom.Id), row => Assert.True(row.AreTogglesEnabled));

            Invoke(window, "OnCancelClick", new object(), new RoutedEventArgs());
            Assert.All(window.AlertTriggers, row => Assert.True(row.AreTogglesEnabled));

            var builtIn = window.AlertTriggers.First(row => row.Trigger.IsBuiltIn);
            Invoke(window, "OnEditClick", new Button { Tag = builtIn }, new RoutedEventArgs());
            Assert.False(window.AlertTriggers.Single(row => row.Trigger.Id == builtIn.Trigger.Id).AreTogglesEnabled);
            Invoke(window, "OnSaveEditorClick", new object(), new RoutedEventArgs());
            Assert.All(window.AlertTriggers, row => Assert.True(row.AreTogglesEnabled));

            Invoke(window, "OnAddClick", new object(), new RoutedEventArgs());
            Assert.All(window.AlertTriggers, row => Assert.True(row.AreTogglesEnabled));
        });
    }

    [Fact]
    public void EditingOneRowDisablesOnlyItsTableTogglesUntilCancelOrSave()
    {
        RunOnSta(() =>
        {
            var custom = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), custom] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(new EventAlertConfigurationController(settings, catalog, _ => true));

            var customRow = window.AlertTriggers.Single(row => row.Trigger.Id == custom.Id);
            Invoke(window, "OnEditClick", new Button { Tag = customRow }, new RoutedEventArgs());
            Assert.False(window.AlertTriggers.Single(row => row.Trigger.Id == custom.Id).AreTogglesEnabled);
            Assert.All(window.AlertTriggers.Where(row => row.Trigger.Id != custom.Id), row => Assert.True(row.AreTogglesEnabled));

            Invoke(window, "OnCancelClick", new object(), new RoutedEventArgs());
            Assert.All(window.AlertTriggers, row => Assert.True(row.AreTogglesEnabled));

            var builtIn = window.AlertTriggers.First(row => row.Trigger.IsBuiltIn);
            Invoke(window, "OnEditClick", new Button { Tag = builtIn }, new RoutedEventArgs());
            Assert.False(window.AlertTriggers.Single(row => row.Trigger.Id == builtIn.Trigger.Id).AreTogglesEnabled);
            Invoke(window, "OnSaveEditorClick", new object(), new RoutedEventArgs());
            Assert.All(window.AlertTriggers, row => Assert.True(row.AreTogglesEnabled));

            Invoke(window, "OnAddClick", new object(), new RoutedEventArgs());
            Assert.All(window.AlertTriggers, row => Assert.True(row.AreTogglesEnabled));
        });
    }

    [Fact]
    public void EditThenAddUnlocksAllTableTogglesAndCancelKeepsThemUnlocked()
    {
        RunOnSta(() =>
        {
            var first = Custom();
            var second = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), first, second] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(new EventAlertConfigurationController(settings, catalog, _ => true));
            window.Show();
            try
            {
                Edit(window, first.Id);
                Assert.Equal(3, TableToggles(window, first.Id).Count);
                Assert.Equal(3, TableToggles(window, second.Id).Count);
                Assert.All(TableToggles(window, first.Id), toggle => Assert.False(toggle.IsEnabled));
                Assert.All(TableToggles(window, second.Id), toggle => Assert.True(toggle.IsEnabled));

                Invoke(window, "OnAddClick", new object(), new RoutedEventArgs());
                window.UpdateLayout();

                Assert.True(window.IsEditorOpen);
                Assert.Equal("Add trigger", window.EditorTitle);
                Assert.All(window.AlertTriggers, row => Assert.True(row.AreTogglesEnabled));
                Assert.Equal(window.AlertTriggers.Count * 3, TableToggles(window).Count);
                Assert.All(TableToggles(window), toggle => Assert.True(toggle.IsEnabled));

                Invoke(window, "OnCancelClick", new object(), new RoutedEventArgs());
                window.UpdateLayout();
                Assert.False(window.IsEditorOpen);
                Assert.All(TableToggles(window), toggle => Assert.True(toggle.IsEnabled));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SwitchingEditedRowsUnlocksThePreviousRowsControls()
    {
        RunOnSta(() =>
        {
            var first = Custom();
            var second = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), first, second] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(new EventAlertConfigurationController(settings, catalog, _ => true));
            window.Show();
            try
            {
                Edit(window, first.Id);
                Edit(window, second.Id);

                Assert.Equal(3, TableToggles(window, first.Id).Count);
                Assert.Equal(3, TableToggles(window, second.Id).Count);
                Assert.All(TableToggles(window, first.Id), toggle => Assert.True(toggle.IsEnabled));
                Assert.All(TableToggles(window, second.Id), toggle => Assert.False(toggle.IsEnabled));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FailedEditorSaveKeepsEditedRowsTableTogglesDisabled()
    {
        RunOnSta(() =>
        {
            var custom = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), custom] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(new EventAlertConfigurationController(settings, catalog, _ => false));
            window.Show();
            try
            {
                Edit(window, custom.Id);
                Invoke(window, "OnSaveEditorClick", new object(), new RoutedEventArgs());
                window.UpdateLayout();

                Assert.True(window.IsEditorOpen);
                Assert.Equal("Settings save failed.", window.ErrorMessage);
                Assert.Equal(3, TableToggles(window, custom.Id).Count);
                Assert.All(TableToggles(window, custom.Id), toggle => Assert.False(toggle.IsEnabled));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ActualTableControlsFollowCustomAndBuiltInEditorTransitions()
    {
        RunOnSta(() =>
        {
            var custom = Custom();
            var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), custom] };
            var catalog = new EventAlertCatalog(settings.EventAlerts);
            var window = new AlertsWindow(new EventAlertConfigurationController(settings, catalog, _ => true));
            window.Show();
            try
            {
                Edit(window, custom.Id);
                Assert.All(TableToggles(window, custom.Id), toggle => Assert.False(toggle.IsEnabled));

                Invoke(window, "OnCancelClick", new object(), new RoutedEventArgs());
                window.UpdateLayout();
                Assert.All(TableToggles(window, custom.Id), toggle => Assert.True(toggle.IsEnabled));

                Edit(window, custom.Id);
                Invoke(window, "OnSaveEditorClick", new object(), new RoutedEventArgs());
                window.UpdateLayout();
                Assert.False(window.IsEditorOpen);
                Assert.All(TableToggles(window, custom.Id), toggle => Assert.True(toggle.IsEnabled));

                var builtInId = EventAlertBuiltIns.WallBreakSoonId;
                Edit(window, builtInId);
                Assert.All(TableToggles(window, builtInId), toggle => Assert.False(toggle.IsEnabled));

                Invoke(window, "OnSaveEditorClick", new object(), new RoutedEventArgs());
                window.UpdateLayout();
                Assert.All(TableToggles(window, builtInId), toggle => Assert.True(toggle.IsEnabled));

                Invoke(window, "OnAddClick", new object(), new RoutedEventArgs());
                window.UpdateLayout();
                Assert.All(TableToggles(window), toggle => Assert.True(toggle.IsEnabled));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static EventAlertTrigger Custom() => new(
        Guid.NewGuid(), "Custom", "custom", true, "wall-break-soon.wav", "Popup", false);

    private static void Edit(AlertsWindow window, Guid id)
    {
        var row = window.AlertTriggers.Single(item => item.Trigger.Id == id);
        Invoke(window, "OnEditClick", new Button { Tag = row }, new RoutedEventArgs());
        window.UpdateLayout();
    }

    private static IReadOnlyList<CheckBox> TableToggles(AlertsWindow window, Guid? id = null) =>
        FindVisualChildren<CheckBox>(window)
            .Where(checkBox => checkBox.Tag is AlertTriggerRow row
                && (id is null || row.Trigger.Id == id))
            .ToArray();

    private sealed class SoundDirectoryFixture : IDisposable
    {
        public SoundDirectoryFixture(params string[] soundFileNames)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "WurmMiningCounter.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            foreach (var soundFileName in soundFileNames)
                File.WriteAllBytes(Path.Combine(DirectoryPath, soundFileName), []);
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private static void Invoke(AlertsWindow window, string method, params object?[] arguments) =>
        typeof(AlertsWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, arguments);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
                yield return typed;

            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
