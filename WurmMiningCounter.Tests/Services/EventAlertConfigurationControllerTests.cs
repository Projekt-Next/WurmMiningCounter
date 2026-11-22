using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventAlertConfigurationControllerTests
{
    [Fact]
    public void NewCustomEditorDefaultsToPopupDisabledAndBuildPreservesIt()
    {
        var editor = new EventAlertEditorViewModel();

        Assert.True(editor.Enabled);
        Assert.True(editor.SoundEnabled);
        Assert.False(editor.PopupEnabled);
        Assert.False(editor.Build().PopupEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingCustomEditorPreservesPopupEnabled(bool popupEnabled)
    {
        var trigger = Custom("mine some") with { PopupEnabled = popupEnabled };

        var editor = new EventAlertEditorViewModel(trigger);

        Assert.Equal(popupEnabled, editor.PopupEnabled);
        Assert.Equal(popupEnabled, editor.Build().PopupEnabled);
    }

    [Fact]
    public void ExistingBuiltInEditorPreservesPopupEnabled()
    {
        var trigger = EventAlertBuiltIns.Create()[0] with { PopupEnabled = false };

        var editor = new EventAlertEditorViewModel(trigger);

        Assert.False(editor.PopupEnabled);
        Assert.False(editor.Build().PopupEnabled);
    }

    [Fact]
    public void NewCustomEditorPersistsPopupDisabled()
    {
        var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var controller = new EventAlertConfigurationController(settings, catalog, _ => true);
        var editor = new EventAlertEditorViewModel
        {
            Name = "Custom",
            MatchText = "mine some",
            PopupText = "Popup"
        };

        Assert.True(controller.TryAddCustom(editor.Build(), out _));

        Assert.False(settings.EventAlerts.Single(trigger => !trigger.IsBuiltIn).PopupEnabled);
    }

    [Fact]
    public void AlertSavePreservesConfiguredLogLocationsAndAdditionalLocations()
    {
        var rule = new KnownLogLocation
        {
            Id = "manual", Enabled = false, Game = "WurmOnline", Type = "Direct", Path = @"D:\\Wurm\\players"
        };
        var settings = new AppSettings
        {
            EventAlerts = [.. EventAlertBuiltIns.Create()],
            AdditionalWurmDataLocations = [@"E:\\Wurm"],
            KnownLogLocations = [rule]
        };
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var controller = new EventAlertConfigurationController(settings, catalog, candidate =>
        {
            Assert.Equal([@"E:\\Wurm"], candidate.AdditionalWurmDataLocations);
            Assert.Equal("manual", Assert.Single(candidate.KnownLogLocations).Id);
            return true;
        });

        Assert.True(controller.TryAddCustom(Custom("mine some"), out _));
    }

    [Fact]
    public void AddEditDeleteCustomPersistAndUpdateRuntimeSnapshot()
    {
        var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var saves = 0;
        var controller = new EventAlertConfigurationController(settings, catalog, _ => { saves++; return true; });
        var custom = Custom("one");

        Assert.True(controller.TryAddCustom(custom, out _));
        Assert.Contains(catalog.GetSnapshot(), item => item.Id == custom.Id);

        var edited = custom with { Name = "Edited", MatchText = "two", PopupText = "Updated" };
        Assert.True(controller.TryEdit(edited, out _));
        Assert.Equal(edited, Assert.Single(catalog.GetSnapshot().Where(item => item.Id == custom.Id)));

        Assert.True(controller.TryDeleteCustom(custom.Id, out _));
        Assert.DoesNotContain(catalog.GetSnapshot(), item => item.Id == custom.Id);
        Assert.Equal(3, saves);
    }

    [Fact]
    public void BuiltInsPreserveCanonicalDetailsAndCanChangeDeliverySettings()
    {
        var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var controller = new EventAlertConfigurationController(settings, catalog, _ => true);
        var builtIn = catalog.GetSnapshot()[0];

        Assert.True(controller.TryEdit(builtIn with
        {
            Name = "Changed",
            MatchText = "Changed",
            Enabled = false,
            SoundFileName = "custom.wav",
            PopupText = "Changed",
            SoundEnabled = false,
            PopupEnabled = false
        }, out _));
        var actual = Assert.Single(catalog.GetSnapshot().Where(item => item.Id == builtIn.Id));
        Assert.Equal(builtIn.Name, actual.Name);
        Assert.Equal(builtIn.MatchText, actual.MatchText);
        Assert.Equal(builtIn.PopupText, actual.PopupText);
        Assert.False(actual.Enabled);
        Assert.Equal("custom.wav", actual.SoundFileName);
        Assert.False(actual.SoundEnabled);
        Assert.False(actual.PopupEnabled);
        Assert.False(controller.TryDeleteCustom(builtIn.Id, out _));
    }

    [Fact]
    public void EnabledSoundAndPopupCanBeChangedIndependentlyWithoutLosingDetails()
    {
        var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var controller = new EventAlertConfigurationController(settings, catalog, _ => true);
        var trigger = catalog.GetSnapshot()[0];

        Assert.True(controller.TrySetEnabled(trigger.Id, false, out _));
        Assert.True(controller.TrySetSoundEnabled(trigger.Id, false, out _));
        Assert.True(controller.TrySetPopupEnabled(trigger.Id, false, out _));

        var actual = catalog.GetSnapshot()[0];
        Assert.False(actual.Enabled);
        Assert.False(actual.SoundEnabled);
        Assert.False(actual.PopupEnabled);
        Assert.Equal(trigger.SoundFileName, actual.SoundFileName);
        Assert.Equal(trigger.PopupText, actual.PopupText);
    }

    [Fact]
    public void FailedSaveLeavesSettingsAndRuntimeUnchanged()
    {
        var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var before = catalog.GetSnapshot();
        var controller = new EventAlertConfigurationController(settings, catalog, _ => false);

        Assert.False(controller.TryAddCustom(Custom("one"), out var error));
        Assert.Equal("Settings save failed.", error);
        Assert.Equal(before, catalog.GetSnapshot());
        Assert.Equal(2, settings.EventAlerts.Count);
    }

    [Theory]
    [InlineData("o", false)]
    [InlineData("or", false)]
    [InlineData("ore", true)]
    [InlineData("mine some", true)]
    [InlineData("   ", false)]
    public void CustomPatternValidationIsEnforcedBeforeSaveAndCatalogUpdate(string pattern, bool expectedSuccess)
    {
        var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var saves = 0;
        var controller = new EventAlertConfigurationController(settings, catalog, _ => { saves++; return true; });

        var success = controller.TryAddCustom(Custom(pattern), out var error);

        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess)
        {
            Assert.Null(error);
            Assert.Equal(1, saves);
            Assert.Equal(3, catalog.GetSnapshot().Length);
        }
        else
        {
            Assert.Equal("Trigger must contain one word with at least 3 characters or at least two words.", error);
            Assert.Equal(0, saves);
            Assert.Equal(EventAlertBuiltIns.Create(), catalog.GetSnapshot());
            Assert.Equal(EventAlertBuiltIns.Create(), settings.EventAlerts);
        }
    }

    [Fact]
    public void UpdatingAnotherTriggerRetainsLegacyShortCustomPattern()
    {
        var legacy = Custom("a");
        var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), legacy] };
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var controller = new EventAlertConfigurationController(settings, catalog, _ => true);

        Assert.True(controller.TrySetEnabled(EventAlertBuiltIns.WallBreakId, false, out _));

        Assert.Contains(catalog.GetSnapshot(), trigger => trigger == legacy);
        Assert.Contains(settings.EventAlerts, trigger => trigger == legacy);
    }

    [Fact]
    public void InvalidCustomEditDoesNotSaveOrReplaceTheRuntimeTrigger()
    {
        var custom = Custom("mine some");
        var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create(), custom] };
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var saves = 0;
        var controller = new EventAlertConfigurationController(settings, catalog, _ => { saves++; return true; });

        Assert.False(controller.TryEdit(custom with { MatchText = "or" }, out var error));

        Assert.Equal("Trigger must contain one word with at least 3 characters or at least two words.", error);
        Assert.Equal(0, saves);
        Assert.Equal(custom, catalog.GetSnapshot().Single(trigger => trigger.Id == custom.Id));
        Assert.Equal(custom, settings.EventAlerts.Single(trigger => trigger.Id == custom.Id));
    }

    [Fact]
    public void SavedChangeAffectsNextAlertModuleProcess()
    {
        var settings = new AppSettings { EventAlerts = [.. EventAlertBuiltIns.Create()] };
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var controller = new EventAlertConfigurationController(settings, catalog, _ => true);
        var custom = Custom("custom line");
        Assert.True(controller.TryAddCustom(custom, out _));
        var notifications = new List<EventAlertNotification>();
        var module = new EventAlertModule(catalog, notifications.Add);

        module.Process(new OrderedEventLine(EventTransportTestProfileIds.Alice, "Alice", 1, new EventLogLine(EventTransportTestProfileIds.Alice, "Alice", "Event.txt", custom.MatchText)));

        Assert.Equal(custom.Id, Assert.Single(notifications).Trigger.Id);
    }

    [Fact]
    public void BuiltInEditorOnlyEmitsDeliveryChangesAndCustomEditorRetainsIdentity()
    {
        var builtIn = EventAlertBuiltIns.Create()[0];
        var builtInEditor = new EventAlertEditorViewModel(builtIn)
        {
            Name = "Changed",
            MatchText = "Changed",
            SoundFileName = "custom.wav",
            PopupText = "Changed",
            Enabled = false,
            SoundEnabled = false,
            PopupEnabled = false
        };
        var custom = Custom("one");
        var customEditor = new EventAlertEditorViewModel(custom) { Name = "Edited" };

        Assert.Equal(builtIn with
        {
            SoundFileName = "custom.wav",
            Enabled = false,
            SoundEnabled = false,
            PopupEnabled = false
        }, builtInEditor.Build());
        Assert.Equal(custom.Id, customEditor.Build().Id);
        Assert.True(builtInEditor.IsBuiltIn);
        Assert.False(builtInEditor.CanEditDetails);
    }

    private static EventAlertTrigger Custom(string text) => new(
        Guid.NewGuid(), "Custom", text, Enabled: true, null, "Popup", IsBuiltIn: false);
}
