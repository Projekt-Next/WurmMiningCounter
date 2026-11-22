using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class AppSettingsAutoSaveTests
{
    [Fact]
    public void SavingOtherSettingsPreservesAdditionalWurmDataLocations()
    {
        var currentSettings = new AppSettings
        {
            AdditionalWurmDataLocations = [@"D:\\WurmData"]
        };

        var saved = App.TryApplySettingsChanges(
            currentSettings,
            new AppSettings { AlwaysOnTop = true },
            candidate =>
            {
                Assert.Equal([@"D:\\WurmData"], candidate.AdditionalWurmDataLocations);
                return true;
            });

        Assert.True(saved);
        Assert.Equal([@"D:\\WurmData"], currentSettings.AdditionalWurmDataLocations);
    }

    [Fact]
    public void IntentionalLocationEditReplacesBothLocationListsIncludingAnEmptyList()
    {
        var currentSettings = new AppSettings
        {
            AdditionalWurmDataLocations = [@"D:\\WurmData"],
            KnownLogLocations = [new KnownLogLocation { Id = "old", Enabled = true, Game = "WurmOnline", Type = KnownLogLocations.DirectType, Path = @"D:\\Wurm\\players" }]
        };
        var changedSettings = new AppSettings
        {
            AdditionalWurmDataLocations = [],
            KnownLogLocations = [],
            LocationSettingsChanged = true
        };

        var saved = App.TryApplySettingsChanges(currentSettings, changedSettings, candidate =>
        {
            Assert.Empty(candidate.AdditionalWurmDataLocations);
            Assert.Empty(candidate.KnownLogLocations);
            return true;
        });

        Assert.True(saved);
        Assert.Empty(currentSettings.AdditionalWurmDataLocations);
        Assert.Empty(currentSettings.KnownLogLocations);
    }

    [Fact]
    public void ApplyingSettingsChangesPreservesWindowPlacement()
    {
        var placement = new WindowPlacement(100, 200, 640);
        var currentSettings = new AppSettings
        {
            AlwaysOnTop = false,
            StartMinimizedToTray = false,
            WindowPlacement = placement
        };

        App.ApplySettingsChanges(
            currentSettings,
            new AppSettings
            {
                AlwaysOnTop = true,
                StartMinimizedToTray = true
            });

        Assert.True(currentSettings.AlwaysOnTop);
        Assert.True(currentSettings.StartMinimizedToTray);
        Assert.Same(placement, currentSettings.WindowPlacement);
    }

    [Fact]
    public void TryApplyingSettingsChangesAppliesInMemorySettingsAfterSuccessfulSave()
    {
        var currentSettings = new AppSettings
        {
            AlwaysOnTop = false,
            StartMinimizedToTray = false
        };

        var saved = App.TryApplySettingsChanges(
            currentSettings,
            new AppSettings { AlwaysOnTop = true, StartMinimizedToTray = true },
            _ => true);

        Assert.True(saved);
        Assert.True(currentSettings.AlwaysOnTop);
        Assert.True(currentSettings.StartMinimizedToTray);
    }

    [Fact]
    public void TryApplyingSettingsChangesKeepsInMemorySettingsWhenSaveFails()
    {
        var placement = new WindowPlacement(100, 200, 640);
        var currentSettings = new AppSettings
        {
            AlwaysOnTop = false,
            StartMinimizedToTray = false,
            WindowPlacement = placement
        };

        var saved = App.TryApplySettingsChanges(
            currentSettings,
            new AppSettings { AlwaysOnTop = true, StartMinimizedToTray = true },
            _ => false);

        Assert.False(saved);
        Assert.False(currentSettings.AlwaysOnTop);
        Assert.False(currentSettings.StartMinimizedToTray);
        Assert.Same(placement, currentSettings.WindowPlacement);
    }

    [Fact]
    public void ApplyAndPlacementSavesPreserveEventAlerts()
    {
        var placement = new WindowPlacement(100, 200, 640);
        var trigger = new EventAlertTrigger(
            Guid.NewGuid(), "Custom", "text", true, null, "popup", false);
        var currentSettings = new AppSettings { EventAlerts = [trigger], WindowPlacement = placement };
        App.ApplySettingsChanges(currentSettings, new AppSettings { EventAlerts = [trigger] });

        Assert.Equal(trigger, Assert.Single(currentSettings.EventAlerts.Where(item => !item.IsBuiltIn)));

        var saved = App.TryApplySettingsChanges(currentSettings, currentSettings, candidate =>
        {
            Assert.Equal(trigger, Assert.Single(candidate.EventAlerts.Where(item => !item.IsBuiltIn)));
            Assert.Same(placement, candidate.WindowPlacement);
            return true;
        });
        Assert.True(saved);
    }

    [Fact]
    public void SavingWindowPlacementPreservesCustomAndBuiltInAlerts()
    {
        var originalPlacement = new WindowPlacement(100, 200, 640);
        var savedPlacement = new WindowPlacement(120, 240, 800);
        var custom = new EventAlertTrigger(
            Guid.NewGuid(), "Custom", "Custom line", true, "custom.wav", "Custom popup", false);
        var currentSettings = new AppSettings
        {
            EventAlerts = [.. EventAlertBuiltIns.Create(), custom],
            WindowPlacement = originalPlacement
        };
        AppSettings? savedCandidate = null;

        var saved = App.TrySaveWindowPlacement(currentSettings, savedPlacement, candidate =>
        {
            savedCandidate = candidate;
            return true;
        });

        Assert.True(saved);
        var candidate = Assert.IsType<AppSettings>(savedCandidate);
        Assert.Equal(savedPlacement, candidate.WindowPlacement);
        Assert.Equal(custom, Assert.Single(candidate.EventAlerts.Where(trigger => trigger.Id == custom.Id)));
        Assert.Equal(EventAlertBuiltIns.Create(), candidate.EventAlerts.Take(2));
        Assert.Equal(savedPlacement, currentSettings.WindowPlacement);
    }
}
