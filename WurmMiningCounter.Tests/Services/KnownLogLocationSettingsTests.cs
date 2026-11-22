using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class KnownLogLocationSettingsTests
{
    [Fact]
    public void FirstRuntimeLoadCreatesSettingsWithDefaultLocationRules()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "WurmMiningCounter.Tests", Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("WURM_MINING_COUNTER_SETTINGS_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("WURM_MINING_COUNTER_SETTINGS_DIRECTORY", directoryPath);

            var settings = new SettingsService().Load();

            Assert.True(File.Exists(Path.Combine(directoryPath, "settings.json")));
            Assert.Equal(["wurm-online-standard", "wurm-online-steam", "wurm-unlimited-steam"], settings.KnownLogLocations.Select(location => location.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WURM_MINING_COUNTER_SETTINGS_DIRECTORY", previous);
            if (Directory.Exists(directoryPath))
                Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public void LegacySettingsReceiveDefaultsInMemoryAndPersistOnNormalSave()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.FilePath, "{\"AlwaysOnTop\":true}");

        var settings = fixture.Service.Load();

        Assert.True(settings.AlwaysOnTop);
        Assert.Equal(3, settings.KnownLogLocations.Count);
        Assert.Equal("{\"AlwaysOnTop\":true}", File.ReadAllText(fixture.FilePath));
        Assert.True(fixture.Service.Save(settings));
        Assert.Contains("KnownLogLocations", File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public void ExistingManualRulesIncludingDisabledAndDeletedDefaultsAreRetained()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.FilePath, """
            { "KnownLogLocations": [
              { "Id":"custom", "Enabled":false, "Game":"WurmOnline", "Type":"Direct", "Path":"D:\\logs" }
            ] }
            """);

        var settings = fixture.Service.Load();
        settings.AlwaysOnTop = true;
        Assert.True(fixture.Service.Save(settings));

        var saved = fixture.Service.Load();
        var location = Assert.Single(saved.KnownLogLocations);
        Assert.Equal("custom", location.Id);
        Assert.False(location.Enabled);
    }

    [Fact]
    public void CorruptedExistingFileIsNotOverwrittenDuringLoad()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.DirectoryPath);
        const string invalid = "{ invalid json }";
        File.WriteAllText(fixture.FilePath, invalid);

        var settings = fixture.Service.Load();

        Assert.Equal(3, settings.KnownLogLocations.Count);
        Assert.Equal(invalid, File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public void CorruptedExistingFileBlocksLaterSaveWithoutReplacingIt()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.DirectoryPath);
        const string invalid = "{ invalid json }";
        File.WriteAllText(fixture.FilePath, invalid);

        fixture.Service.Load();

        Assert.False(fixture.Service.Save(new AppSettings { AlwaysOnTop = true }));
        Assert.Equal(invalid, File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public void UnreadableExistingFileBlocksLaterSave()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.FilePath, "{\"AlwaysOnTop\":true}");

        using (var lockStream = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fixture.Service.Load();
        }

        Assert.False(fixture.Service.Save(new AppSettings { AlwaysOnTop = false }));
        Assert.Equal("{\"AlwaysOnTop\":true}", File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public void NullLocationEntriesDoNotPreventLoadOrSaveOfOtherRules()
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.FilePath, """
            { "KnownLogLocations": [null,
              { "Id":"custom", "Enabled":true, "Game":"WurmOnline", "Type":"Direct", "Path":"D:\\logs" }
            ] }
            """);

        var settings = fixture.Service.Load();

        Assert.True(fixture.Service.Save(settings));
        var saved = fixture.Service.Load();
        Assert.Equal("custom", Assert.Single(saved.KnownLogLocations).Id);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"settings\"")]
    public void InvalidRootBlocksAllLaterSettingsWritesAndPreservesOriginalBytes(string json)
    {
        using var fixture = new SettingsServiceFixture();
        Directory.CreateDirectory(fixture.DirectoryPath);
        File.WriteAllText(fixture.FilePath, json);
        var original = File.ReadAllBytes(fixture.FilePath);
        var settings = fixture.Service.Load();

        Assert.False(App.TrySaveWindowPlacement(settings, new WindowPlacement(100, 200, 640), fixture.Service.Save));
        Assert.False(App.TryApplySettingsChanges(settings, new AppSettings { AlwaysOnTop = true }, fixture.Service.Save));
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var controller = new EventAlertConfigurationController(settings, catalog, fixture.Service.Save);
        var trigger = catalog.GetSnapshot()[0];
        Assert.False(controller.TrySetEnabled(trigger.Id, !trigger.Enabled, out _));
        Assert.False(fixture.Service.Save(settings));
        Assert.Equal(original, File.ReadAllBytes(fixture.FilePath));

        // A later successful read must not silently re-enable autosave for this instance.
        File.WriteAllText(fixture.FilePath, "{}");
        var repaired = fixture.Service.Load();
        Assert.False(fixture.Service.Save(repaired));
        Assert.Equal("{}", File.ReadAllText(fixture.FilePath));
    }

    private sealed class SettingsServiceFixture : IDisposable
    {
        public SettingsServiceFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "WurmMiningCounter.Tests", Guid.NewGuid().ToString("N"));
            FilePath = Path.Combine(DirectoryPath, "settings.json");
            Service = new SettingsService(DirectoryPath);
        }

        public string DirectoryPath { get; }
        public string FilePath { get; }
        public SettingsService Service { get; }
        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
