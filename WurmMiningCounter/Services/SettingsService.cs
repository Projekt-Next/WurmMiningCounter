using System.IO;
using System.Text.Json;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class SettingsService
{
    private const string SettingsFileName = "settings.json";
    private const string SettingsDirectoryEnvironmentVariable = "WURM_MINING_COUNTER_SETTINGS_DIRECTORY";
    private readonly object _sync = new();
    private readonly string _settingsDirectoryPath;
    private readonly RuntimeDiagnosticService _diagnostics;
    private bool _writesBlockedByUnreadableSettings;

    public SettingsService()
        : this(GetDefaultSettingsDirectoryPath(), null)
    {
    }

    public SettingsService(RuntimeDiagnosticService diagnostics)
        : this(GetDefaultSettingsDirectoryPath(), diagnostics)
    {
    }

    internal SettingsService(string settingsDirectoryPath, RuntimeDiagnosticService? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectoryPath);
        _settingsDirectoryPath = Path.GetFullPath(settingsDirectoryPath);
        _diagnostics = diagnostics ?? new RuntimeDiagnosticService();
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            var settingsFilePath = GetSettingsFilePath();

            try
            {
                var json = File.ReadAllText(settingsFilePath);
                return DeserializeAndNormalize(json);
            }
            catch (FileNotFoundException)
            {
                var settings = Normalize(new AppSettings());
                TryCreateInitialSettings(settings);
                return settings;
            }
            catch (DirectoryNotFoundException)
            {
                var settings = Normalize(new AppSettings());
                TryCreateInitialSettings(settings);
                return settings;
            }
            catch (JsonException exception)
            {
                _writesBlockedByUnreadableSettings = true;
                _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(SettingsService), "Settings load failed because settings.json is invalid: {0}", exception);
                return Normalize(new AppSettings());
            }
            catch (IOException exception)
            {
                _writesBlockedByUnreadableSettings = true;
                _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(SettingsService), "Settings load failed: {0}", exception);
                return Normalize(new AppSettings());
            }
            catch (UnauthorizedAccessException exception)
            {
                _writesBlockedByUnreadableSettings = true;
                _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(SettingsService), "Settings load was denied: {0}", exception);
                return Normalize(new AppSettings());
            }
        }
    }

    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            if (_writesBlockedByUnreadableSettings)
            {
                _diagnostics.Report(RuntimeDiagnosticLevel.Error, nameof(SettingsService),
                    "Settings save was blocked because the existing settings.json could not be read safely.");
                return false;
            }

            var settingsFilePath = GetSettingsFilePath();
            string? temporaryFilePath = null;

            try
            {
                Directory.CreateDirectory(_settingsDirectoryPath);
                var candidate = CopySettings(settings);
                EventAlertSettingsNormalizer.Normalize(candidate);

                temporaryFilePath = Path.Combine(
                    _settingsDirectoryPath,
                    $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");

                var json = Serialize(candidate);

                File.WriteAllText(temporaryFilePath, json);
                if (!TryValidateCurrentSettingsDocument(settingsFilePath))
                    return false;

                File.Move(temporaryFilePath, settingsFilePath, overwrite: true);
                temporaryFilePath = null;
                settings.EventAlerts = [.. candidate.EventAlerts];
                settings.AdditionalWurmDataLocations = [.. candidate.AdditionalWurmDataLocations];
                settings.KnownLogLocations = KnownLogLocations.CopyAll(candidate.KnownLogLocations);
                return true;
            }
            catch (IOException exception)
            {
                _diagnostics.Report(RuntimeDiagnosticLevel.Error, nameof(SettingsService), "Settings save failed: {0}", exception);
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                _diagnostics.Report(RuntimeDiagnosticLevel.Error, nameof(SettingsService), "Settings save was denied: {0}", exception);
                return false;
            }
            finally
            {
                if (temporaryFilePath is not null)
                {
                    TryDeleteTemporaryFile(temporaryFilePath);
                }
            }
        }
    }

    private string GetSettingsFilePath() => Path.Combine(_settingsDirectoryPath, SettingsFileName);

    private bool TryValidateCurrentSettingsDocument(string settingsFilePath)
    {
        try
        {
            _ = DeserializeAndNormalize(File.ReadAllText(settingsFilePath));
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (JsonException)
        {
            ReportSaveBlockedByCurrentDocument();
            return false;
        }
        catch (IOException)
        {
            ReportSaveBlockedByCurrentDocument();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            ReportSaveBlockedByCurrentDocument();
            return false;
        }
    }

    private void ReportSaveBlockedByCurrentDocument() => _diagnostics.Report(
        RuntimeDiagnosticLevel.Error,
        nameof(SettingsService),
        "Settings save was blocked because the current settings.json could not be read and validated safely.");

    private static AppSettings DeserializeAndNormalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The settings.json root must be a settings object.");

        var settings = JsonSerializer.Deserialize<AppSettings>(json)
            ?? throw new JsonException("The settings.json root must be a settings object, not null.");
        ApplyEventAlertDeliveryDefaults(settings, json);
        ApplyKnownLogLocationDefaults(settings, json);
        return Normalize(settings);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        EventAlertSettingsNormalizer.Normalize(settings);
        return settings;
    }

    private static AppSettings CopySettings(AppSettings settings) => new()
    {
        AlwaysOnTop = settings.AlwaysOnTop,
        StartMinimizedToTray = settings.StartMinimizedToTray,
        DiagnosticsEnabled = settings.DiagnosticsEnabled,
        EventAlerts = [.. settings.EventAlerts],
        AdditionalWurmDataLocations = [.. settings.AdditionalWurmDataLocations],
        KnownLogLocations = KnownLogLocations.CopyAll(settings.KnownLogLocations),
        WindowPlacement = settings.WindowPlacement
    };

    private bool TryCreateInitialSettings(AppSettings settings)
    {
        var settingsFilePath = GetSettingsFilePath();
        string? temporaryFilePath = null;
        try
        {
            var candidate = CopySettings(settings);
            EventAlertSettingsNormalizer.Normalize(candidate);
            Directory.CreateDirectory(_settingsDirectoryPath);
            temporaryFilePath = Path.Combine(_settingsDirectoryPath, $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryFilePath, Serialize(candidate));
            File.Move(temporaryFilePath, settingsFilePath);
            temporaryFilePath = null;
            settings.EventAlerts = [.. candidate.EventAlerts];
            settings.AdditionalWurmDataLocations = [.. candidate.AdditionalWurmDataLocations];
            settings.KnownLogLocations = KnownLogLocations.CopyAll(candidate.KnownLogLocations);
            return true;
        }
        catch (IOException exception)
        {
            _writesBlockedByUnreadableSettings = true;
            _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(SettingsService),
                "Initial settings creation failed without replacing an existing settings.json: {0}", exception);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            _writesBlockedByUnreadableSettings = true;
            _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(SettingsService),
                "Initial settings creation was denied without replacing an existing settings.json: {0}", exception);
            return false;
        }
        finally
        {
            if (temporaryFilePath is not null)
                TryDeleteTemporaryFile(temporaryFilePath);
        }
    }

    private static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    // A valid pre-LD-2A1 file acquires defaults in memory and on its next normal
    // save. An existing property, including an empty list, is the user's choice.
    private static void ApplyKnownLogLocationDefaults(AppSettings? settings, string json)
    {
        if (settings is null)
            return;

        using var document = JsonDocument.Parse(json);
        if (!HasProperty(document.RootElement, "KnownLogLocations"))
            settings.KnownLogLocations = KnownLogLocations.CreateDefaults();
    }

    // The first Event Alerts format did not persist these opt-in delivery switches.
    // Missing values must retain the former behavior rather than deserialize as false.
    private static void ApplyEventAlertDeliveryDefaults(AppSettings? settings, string json)
    {
        if (settings is null)
            return;

        using var document = JsonDocument.Parse(json);
        if (!TryGetProperty(document.RootElement, "EventAlerts", out var alerts)
            || alerts.ValueKind != JsonValueKind.Array)
            return;

        var serialized = alerts.EnumerateArray().ToArray();
        for (var index = 0; index < settings.EventAlerts.Count && index < serialized.Length; index++)
        {
            var trigger = settings.EventAlerts[index];
            var item = serialized[index];
            if (trigger is null || item.ValueKind != JsonValueKind.Object)
                continue;

            var soundFileName = HasProperty(item, "SoundFileName")
                ? trigger.SoundFileName
                : ReadLegacySoundFileName(item);
            settings.EventAlerts[index] = trigger with
            {
                SoundEnabled = HasBooleanProperty(item, "SoundEnabled")
                    ? trigger.SoundEnabled
                    : !string.IsNullOrWhiteSpace(soundFileName),
                PopupEnabled = HasBooleanProperty(item, "PopupEnabled")
                    ? trigger.PopupEnabled
                    : true,
                SoundFileName = soundFileName
            };
        }
    }

    private static string? ReadLegacySoundFileName(JsonElement item)
    {
        if (!TryGetProperty(item, "Sound", out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var legacySound) => legacySound switch
            {
                1 => "wall-break-soon.wav",
                2 => "vein-destroyed.wav",
                _ => null
            },
            JsonValueKind.String when string.Equals(value.GetString(), "WallBreakSoon", StringComparison.Ordinal) => "wall-break-soon.wav",
            JsonValueKind.String when string.Equals(value.GetString(), "VeinDestroyed", StringComparison.Ordinal) => "vein-destroyed.wav",
            _ => null
        };
    }

    private static bool HasBooleanProperty(JsonElement root, string propertyName) =>
        TryGetProperty(root, propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool HasProperty(JsonElement root, string propertyName) =>
        TryGetProperty(root, propertyName, out _);

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    internal static string GetDefaultSettingsDirectoryPath()
    {
        var configuredDirectoryPath = Environment.GetEnvironmentVariable(SettingsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredDirectoryPath))
            return configuredDirectoryPath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WurmMiningCounter");
    }

    private void TryDeleteTemporaryFile(string temporaryFilePath)
    {
        try
        {
            File.Delete(temporaryFilePath);
        }
        catch (IOException exception)
        {
            _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(SettingsService), "Settings temporary-file cleanup failed: {0}", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            _diagnostics.Report(RuntimeDiagnosticLevel.Warning, nameof(SettingsService), "Settings temporary-file cleanup was denied: {0}", exception);
        }
    }
}
