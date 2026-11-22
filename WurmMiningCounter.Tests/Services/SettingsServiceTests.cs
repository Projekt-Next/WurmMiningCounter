using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class SettingsServiceTests
{
    [Fact]
    public void AdditionalWurmDataLocationsRoundTripThroughSettingsJson()
    {
        using var fixture = new SettingsServiceFixture();
        var locations = new List<string> { @"D:\\WurmData", @"E:\\players" };

        Assert.True(fixture.Service.Save(new AppSettings { AdditionalWurmDataLocations = locations }));

        var loaded = fixture.Service.Load();
        Assert.Equal(locations, loaded.AdditionalWurmDataLocations);
    }

    [Fact]
    public void LegacySettingsJsonWithoutAdditionalLocationsLoadsAnEmptyList()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.SettingsDirectoryPath);
        File.WriteAllText(fixture.SettingsFilePath, "{\"AlwaysOnTop\":true}");

        var settings = fixture.Service.Load();

        Assert.True(settings.AlwaysOnTop);
        Assert.Empty(settings.AdditionalWurmDataLocations);
    }

    [Fact]
    public void DefaultSettingsHaveNoWindowPlacement()
    {
        var settings = new AppSettings();

        Assert.Null(settings.WindowPlacement);
    }

    [Fact]
    public void LoadWithoutSettingsFileReturnsDefaults()
    {
        using var fixture = new SettingsServiceFixture();

        var settings = fixture.Service.Load();

        Assert.False(settings.AlwaysOnTop);
        Assert.False(settings.StartMinimizedToTray);
        Assert.Equal(2, settings.EventAlerts.Count);
    }

    [Fact]
    public void SaveAndLoadPreservesAllValues()
    {
        using var fixture = new SettingsServiceFixture();
        var expected = new AppSettings
        {
            AlwaysOnTop = true,
            StartMinimizedToTray = true
        };

        Assert.True(fixture.Service.Save(expected));

        var actual = fixture.Service.Load();

        Assert.True(actual.AlwaysOnTop);
        Assert.True(actual.StartMinimizedToTray);
        Assert.Equal(2, actual.EventAlerts.Count);
    }

    [Fact]
    public void SaveAndLoadPreservesWindowPlacement()
    {
        using var fixture = new SettingsServiceFixture();
        var expectedPlacement = new WindowPlacement(120.5, 240.25, 640.75);

        Assert.True(fixture.Service.Save(new AppSettings
        {
            AlwaysOnTop = true,
            StartMinimizedToTray = true,
            WindowPlacement = expectedPlacement
        }));

        var actual = fixture.Service.Load();

        var actualPlacement = Assert.IsType<WindowPlacement>(actual.WindowPlacement);
        Assert.Equal(expectedPlacement.Left, actualPlacement.Left);
        Assert.Equal(expectedPlacement.Top, actualPlacement.Top);
        Assert.Equal(expectedPlacement.Width, actualPlacement.Width);
        Assert.True(actual.AlwaysOnTop);
        Assert.True(actual.StartMinimizedToTray);
    }

    [Fact]
    public void LoadWithLegacyHideOnCloseRemainsCompatible()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.SettingsDirectoryPath);
        File.WriteAllText(
            fixture.SettingsFilePath,
            "{\"AlwaysOnTop\":true,\"StartMinimizedToTray\":true,\"HideOnClose\":true,\"WindowPlacement\":{\"Left\":120,\"Top\":240,\"Width\":640,\"Height\":480}}");

        var settings = fixture.Service.Load();

        Assert.True(settings.AlwaysOnTop);
        Assert.True(settings.StartMinimizedToTray);
        Assert.Equal(2, settings.EventAlerts.Count);
        var placement = Assert.IsType<WindowPlacement>(settings.WindowPlacement);
        Assert.Equal(120, placement.Left);
        Assert.Equal(240, placement.Top);
        Assert.Equal(640, placement.Width);

        fixture.Service.Save(settings);

        var savedJson = File.ReadAllText(fixture.SettingsFilePath);
        Assert.DoesNotContain("\"Height\"", savedJson);
    }

    [Fact]
    public void SaveWithNullSettingsThrowsArgumentNullException()
    {
        using var fixture = new SettingsServiceFixture();

        Assert.Throws<ArgumentNullException>(() => fixture.Service.Save(null!));
    }

    [Fact]
    public void LoadWithCorruptedJsonReturnsDefaults()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.SettingsDirectoryPath);
        File.WriteAllText(fixture.SettingsFilePath, "{ invalid json }");

        var settings = fixture.Service.Load();

        Assert.False(settings.AlwaysOnTop);
        Assert.False(settings.StartMinimizedToTray);
    }

    [Fact]
    public void SaveAfterValidLoadRejectsExternallyCorruptedSettingsWithoutChangingItsBytes()
    {
        using var fixture = new SettingsServiceFixture();
        Assert.True(fixture.Service.Save(new AppSettings { AlwaysOnTop = true }));
        _ = fixture.Service.Load();
        const string corrupted = "{ malformed externally }";
        File.WriteAllText(fixture.SettingsFilePath, corrupted);

        Assert.False(fixture.Service.Save(new AppSettings { StartMinimizedToTray = true }));

        Assert.Equal(corrupted, File.ReadAllText(fixture.SettingsFilePath));
    }

    [Fact]
    public void SaveAfterInvalidLoadRemainsRejectedWithoutChangingItsBytes()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.SettingsDirectoryPath);
        const string corrupted = "{ malformed at load }";
        File.WriteAllText(fixture.SettingsFilePath, corrupted);

        _ = fixture.Service.Load();

        Assert.False(fixture.Service.Save(new AppSettings { AlwaysOnTop = true }));
        Assert.Equal(corrupted, File.ReadAllText(fixture.SettingsFilePath));
    }

    [Fact]
    public void SaveAfterValidExternalSettingsChangeUsesTheNormalSaveSemantics()
    {
        using var fixture = new SettingsServiceFixture();
        Assert.True(fixture.Service.Save(new AppSettings { AlwaysOnTop = true }));
        _ = fixture.Service.Load();
        File.WriteAllText(fixture.SettingsFilePath, "{\"AlwaysOnTop\":false,\"StartMinimizedToTray\":true}");

        Assert.True(fixture.Service.Save(new AppSettings { DiagnosticsEnabled = false }));

        var saved = fixture.Service.Load();
        Assert.False(saved.AlwaysOnTop);
        Assert.False(saved.StartMinimizedToTray);
        Assert.False(saved.DiagnosticsEnabled);
    }

    [Fact]
    public void LoadWithLockedSettingsFileReturnsDefaults()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.SettingsDirectoryPath);
        File.WriteAllText(fixture.SettingsFilePath, "{\"AlwaysOnTop\":true}");

        using var lockStream = new FileStream(
            fixture.SettingsFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var settings = fixture.Service.Load();

        Assert.False(settings.AlwaysOnTop);
        Assert.False(settings.StartMinimizedToTray);
    }

    [Fact]
    public void SaveReturnsFalseWhenSettingsDirectoryPathIsAnExistingFile()
    {
        var settingsDirectoryPath = Path.Combine(Path.GetTempPath(), $"WurmMiningCounter.Tests.{Guid.NewGuid():N}");
        File.WriteAllText(settingsDirectoryPath, "not a directory");
        try
        {
            var service = new SettingsService(settingsDirectoryPath);

            Assert.False(service.Save(new AppSettings { AlwaysOnTop = true }));
        }
        finally
        {
            File.Delete(settingsDirectoryPath);
        }
    }

    [Fact]
    public void FailedReplacePreservesExistingSettings()
    {
        using var fixture = new SettingsServiceFixture();
        Assert.True(fixture.Service.Save(new AppSettings { AlwaysOnTop = true }));

        using (var lockStream = new FileStream(
                   fixture.SettingsFilePath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            Assert.False(fixture.Service.Save(new AppSettings { StartMinimizedToTray = true }));
        }

        var settings = fixture.Service.Load();

        Assert.True(settings.AlwaysOnTop);
        Assert.False(settings.StartMinimizedToTray);
    }

    [Fact]
    public void SaveRejectsAnUnreadableExistingDocumentWithoutReplacingIt()
    {
        using var fixture = new SettingsServiceFixture();
        Assert.True(fixture.Service.Save(new AppSettings { AlwaysOnTop = true }));
        var originalBytes = File.ReadAllBytes(fixture.SettingsFilePath);

        using (var lockStream = new FileStream(
                   fixture.SettingsFilePath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            Assert.False(fixture.Service.Save(new AppSettings { StartMinimizedToTray = true }));
        }

        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.SettingsFilePath));
    }

    [Fact]
    public void FailedSaveDoesNotNormalizeOrMutateCallerEventAlerts()
    {
        using var fixture = new SettingsServiceFixture();
        Assert.True(fixture.Service.Save(new AppSettings()));
        var invalidCustom = new EventAlertTrigger(
            Guid.Empty, "Invalid", "Invalid", true, null, "Popup", false);
        var callerSettings = new AppSettings { EventAlerts = [invalidCustom] };

        using (var lockStream = new FileStream(
                   fixture.SettingsFilePath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            Assert.False(fixture.Service.Save(callerSettings));
        }

        Assert.Equal([invalidCustom], callerSettings.EventAlerts);
    }

    [Fact]
    public void SuccessfulSavePublishesNormalizedEventAlertsToCaller()
    {
        using var fixture = new SettingsServiceFixture();
        var callerSettings = new AppSettings
        {
            EventAlerts =
            [
                new EventAlertTrigger(Guid.Empty, "Invalid", "Invalid", true, null, "Popup", false)
            ]
        };

        Assert.True(fixture.Service.Save(callerSettings));

        Assert.Equal(EventAlertBuiltIns.Create(), callerSettings.EventAlerts);
    }

    [Fact]
    public void SaveCreatesMissingDirectory()
    {
        using var fixture = new SettingsServiceFixture();

        Assert.True(fixture.Service.Save(new AppSettings()));

        Assert.True(Directory.Exists(fixture.SettingsDirectoryPath));
        Assert.True(File.Exists(fixture.SettingsFilePath));
    }

    [Fact]
    public void SubsequentSaveReplacesPreviousSettings()
    {
        using var fixture = new SettingsServiceFixture();
        fixture.Service.Save(new AppSettings { AlwaysOnTop = true });

        fixture.Service.Save(new AppSettings
        {
            StartMinimizedToTray = true
        });

        var settings = fixture.Service.Load();

        Assert.False(settings.AlwaysOnTop);
        Assert.True(settings.StartMinimizedToTray);
    }

    [Fact]
    public void SuccessfulSaveDoesNotLeaveTemporaryFile()
    {
        using var fixture = new SettingsServiceFixture();

        fixture.Service.Save(new AppSettings());

        Assert.Empty(Directory.EnumerateFiles(fixture.SettingsDirectoryPath, ".settings.json.*.tmp"));
    }

    [Fact]
    public void LoadReturnsIndependentMutableInstances()
    {
        using var fixture = new SettingsServiceFixture();
        fixture.Service.Save(new AppSettings { AlwaysOnTop = true });

        var first = fixture.Service.Load();
        first.AlwaysOnTop = false;
        var second = fixture.Service.Load();

        Assert.NotSame(first, second);
        Assert.True(second.AlwaysOnTop);
    }

    private sealed class SettingsServiceFixture : IDisposable
    {
        public SettingsServiceFixture()
        {
            SettingsDirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "WurmMiningCounter.Tests",
                Guid.NewGuid().ToString("N"));
            SettingsFilePath = Path.Combine(SettingsDirectoryPath, "settings.json");
            Service = new SettingsService(SettingsDirectoryPath);
        }

        public string SettingsDirectoryPath { get; }

        public string SettingsFilePath { get; }

        public SettingsService Service { get; }

        public void Dispose()
        {
            if (Directory.Exists(SettingsDirectoryPath))
            {
                Directory.Delete(SettingsDirectoryPath, recursive: true);
            }
        }
    }
}
