using System.Text.Json;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventAlertSettingsPersistenceTests
{
    [Fact]
    public void CustomTriggersRoundTripThroughSettingsService()
    {
        using var fixture = new SettingsFixture();
        var custom = CustomTrigger();

        Assert.True(fixture.Service.Save(new AppSettings { EventAlerts = [custom] }));

        var settings = fixture.Service.Load();

        Assert.Equal(custom, Assert.Single(settings.EventAlerts.Where(trigger => trigger.Id == custom.Id)));
        Assert.Equal(3, settings.EventAlerts.Count);
    }

    [Fact]
    public void PersistedBuiltInsAreRestoredToCanonicalDefinition()
    {
        using var fixture = new SettingsFixture();
        fixture.Write(new AppSettings
        {
            EventAlerts =
            [
                new EventAlertTrigger(
                    EventAlertBuiltIns.WallBreakSoonId,
                    "Tampered",
                    "wrong text",
                    Enabled: false,
                    null,
                    "wrong popup",
                    IsBuiltIn: true)
            ]
        });

        var trigger = BuiltIn(fixture.Service.Load(), EventAlertBuiltIns.WallBreakSoonId);
        var canonical = EventAlertBuiltIns.Create()[0];

        Assert.Equal(canonical with { Enabled = false, SoundFileName = null }, trigger);
    }

    [Fact]
    public void BuiltInSoundFileNamePersistsWhileCanonicalFieldsAreRestored()
    {
        using var fixture = new SettingsFixture();
        var settings = fixture.Service.Load();
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var controller = new EventAlertConfigurationController(settings, catalog, fixture.Service.Save);
        var builtIn = catalog.GetSnapshot()[0];

        Assert.True(controller.TryEdit(builtIn with
        {
            Name = "Tampered",
            MatchText = "Tampered",
            PopupText = "Tampered",
            SoundFileName = "custom.wav"
        }, out _));

        var saved = catalog.GetSnapshot()[0];
        Assert.Equal("custom.wav", saved.SoundFileName);
        Assert.Equal(builtIn.Name, saved.Name);
        Assert.Equal(builtIn.MatchText, saved.MatchText);
        Assert.Equal(builtIn.PopupText, saved.PopupText);

        var reloaded = BuiltIn(fixture.Service.Load(), builtIn.Id);
        Assert.Equal("custom.wav", reloaded.SoundFileName);
        Assert.Equal(builtIn.Name, reloaded.Name);
        Assert.Equal(builtIn.MatchText, reloaded.MatchText);
        Assert.Equal(builtIn.PopupText, reloaded.PopupText);
    }

    [Fact]
    public void PersistedTriggersWithoutDeliverySwitchesKeepPreviousDeliveryBehavior()
    {
        using var fixture = new SettingsFixture();
        fixture.WriteJson("""
            {
              "EventAlerts": [
                {
                  "Id": "5c61b657-516b-433a-9c7f-040b6d6c9197",
                  "Name": "Old built-in",
                  "MatchText": "Old text",
                  "Enabled": true,
                  "Sound": 1,
                  "PopupText": "Old popup",
                  "IsBuiltIn": true
                },
                {
                  "Id": "11111111-1111-1111-1111-111111111111",
                  "Name": "Silent custom",
                  "MatchText": "custom",
                  "Enabled": true,
                  "Sound": 0,
                  "PopupText": "Custom popup",
                  "IsBuiltIn": false
                }
              ]
            }
            """);

        var settings = fixture.Service.Load();
        var builtIn = BuiltIn(settings, EventAlertBuiltIns.WallBreakSoonId);
        var custom = settings.EventAlerts.Single(trigger => !trigger.IsBuiltIn);

        Assert.True(builtIn.SoundEnabled);
        Assert.True(builtIn.PopupEnabled);
        Assert.Equal("wall-break-soon.wav", builtIn.SoundFileName);
        Assert.False(custom.SoundEnabled);
        Assert.True(custom.PopupEnabled);
        Assert.Null(custom.SoundFileName);
    }

    [Fact]
    public void CustomSoundFileNameRoundTripsThroughSettingsService()
    {
        using var fixture = new SettingsFixture();
        var custom = CustomTrigger() with { SoundFileName = "custom.wav", SoundEnabled = true };

        Assert.True(fixture.Service.Save(new AppSettings { EventAlerts = [custom] }));

        var loaded = fixture.Service.Load().EventAlerts.Single(trigger => trigger.Id == custom.Id);

        Assert.Equal("custom.wav", loaded.SoundFileName);
        Assert.True(loaded.SoundEnabled);
        var savedJson = File.ReadAllText(fixture.FilePath);
        Assert.Contains("\"SoundFileName\": \"custom.wav\"", savedJson);
        Assert.DoesNotContain("\"Sound\"", savedJson);
    }

    [Fact]
    public void InvalidDuplicatesAndBuiltInCollisionsAreDiscarded()
    {
        using var fixture = new SettingsFixture();
        var duplicate = CustomTrigger();
        fixture.Write(new AppSettings
        {
            EventAlerts =
            [
                duplicate,
                duplicate,
                duplicate with { Id = EventAlertBuiltIns.WallBreakId },
                duplicate with { Id = Guid.NewGuid(), IsBuiltIn = true },
                duplicate with { Id = Guid.Empty },
                duplicate with { Id = Guid.NewGuid(), Name = " " },
                duplicate with { Id = Guid.NewGuid(), MatchText = " " }
            ]
        });

        var settings = fixture.Service.Load();

        Assert.Equal(2, settings.EventAlerts.Count);
        Assert.All(settings.EventAlerts, trigger => Assert.True(trigger.IsBuiltIn));
    }

    [Fact]
    public void NormalizationIsIdempotentAndCustomOrderIsDeterministic()
    {
        var later = CustomTrigger(Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"));
        var earlier = CustomTrigger(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var settings = new AppSettings { EventAlerts = [later, earlier] };

        var first = EventAlertSettingsNormalizer.Normalize(settings).ToArray();
        var second = EventAlertSettingsNormalizer.Normalize(settings).ToArray();

        Assert.Equal(first, second);
        Assert.Equal([earlier.Id, later.Id], settings.EventAlerts.Skip(2).Select(trigger => trigger.Id));
    }

    [Fact]
    public void NullEventAlertEntryDoesNotEscapeLoad()
    {
        using var fixture = new SettingsFixture();
        fixture.WriteJson("{ \"EventAlerts\": [null] }");

        var exception = Record.Exception(fixture.Service.Load);

        Assert.Null(exception);
        Assert.Equal(EventAlertBuiltIns.Create(), fixture.Service.Load().EventAlerts);
    }

    [Fact]
    public void MixedEventAlertEntriesRetainValidCustomAndDiscardNull()
    {
        using var fixture = new SettingsFixture();
        var custom = CustomTrigger(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        fixture.WriteJson($$"""
            {
              "EventAlerts": [
                {
                  "Id": "{{custom.Id}}",
                  "Name": "{{custom.Name}}",
                  "MatchText": "{{custom.MatchText}}",
                  "Enabled": true,
                  "SoundFileName": null,
                  "PopupText": "{{custom.PopupText}}",
                  "IsBuiltIn": false,
                  "SoundEnabled": true,
                  "PopupEnabled": true
                },
                null
              ]
            }
            """);

        var exception = Record.Exception(fixture.Service.Load);

        Assert.Null(exception);
        var settings = fixture.Service.Load();
        Assert.Equal(custom, Assert.Single(settings.EventAlerts.Where(trigger => trigger.Id == custom.Id)));
        Assert.Equal(3, settings.EventAlerts.Count);
        Assert.Equal(EventAlertBuiltIns.Create(), settings.EventAlerts.Take(2));
    }

    [Fact]
    public void PrimitiveEventAlertEntryFallsBackToDefaultsWithoutEscapingLoad()
    {
        using var fixture = new SettingsFixture();
        fixture.WriteJson("{ \"EventAlerts\": [\"not an alert trigger\"] }");

        var exception = Record.Exception(fixture.Service.Load);

        Assert.Null(exception);
        Assert.Equal(EventAlertBuiltIns.Create(), fixture.Service.Load().EventAlerts);
    }

    [Fact]
    public void LegacyShortCustomPatternLoadsWithoutBeingDiscarded()
    {
        using var fixture = new SettingsFixture();
        var legacy = CustomTrigger() with { MatchText = "a" };
        fixture.Write(new AppSettings { EventAlerts = [legacy] });

        var exception = Record.Exception(fixture.Service.Load);

        Assert.Null(exception);
        Assert.Equal(legacy, Assert.Single(fixture.Service.Load().EventAlerts.Where(trigger => trigger.Id == legacy.Id)));
    }

    private static EventAlertTrigger BuiltIn(AppSettings settings, Guid id) =>
        settings.EventAlerts.Single(trigger => trigger.Id == id);

    private static EventAlertTrigger CustomTrigger(Guid? id = null) => new(
        id ?? Guid.NewGuid(),
        "Custom",
        "Custom line",
        Enabled: true,
        null,
        "Custom popup",
        IsBuiltIn: false);

    private sealed class SettingsFixture : IDisposable
    {
        public SettingsFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "WurmMiningCounter.Tests", Guid.NewGuid().ToString("N"));
            FilePath = Path.Combine(DirectoryPath, "settings.json");
            Service = new SettingsService(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public string FilePath { get; }

        public SettingsService Service { get; }

        public void Write(AppSettings settings) =>
            WriteJson(JsonSerializer.Serialize(settings));

        public void WriteJson(string json)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, json);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
