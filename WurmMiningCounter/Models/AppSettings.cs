namespace WurmMiningCounter.Models;

public sealed class AppSettings
{
    private List<EventAlertTrigger> _eventAlerts = [];
    private List<string> _additionalWurmDataLocations = [];
    private List<KnownLogLocation> _knownLogLocations = global::WurmMiningCounter.Models.KnownLogLocations.CreateDefaults();

    public bool AlwaysOnTop { get; set; } = false;

    public bool StartMinimizedToTray { get; set; } = false;

    public bool DiagnosticsEnabled { get; set; } = true;

    public List<EventAlertTrigger> EventAlerts
    {
        get => _eventAlerts;
        set => _eventAlerts = value ?? [];
    }

    /// <summary>
    /// User-supplied Wurm data locations. Each value may identify a Wurm folder,
    /// its players child, or one profile's logs directory.
    /// </summary>
    public List<string> AdditionalWurmDataLocations
    {
        get => _additionalWurmDataLocations;
        set => _additionalWurmDataLocations = value ?? [];
    }

    /// <summary>
    /// Declarative locations which may resolve to Wurm players directories.
    /// Definitions are retained even when their current target is unavailable.
    /// </summary>
    public List<KnownLogLocation> KnownLogLocations
    {
        get => _knownLogLocations;
        set => _knownLogLocations = value ?? [];
    }

    public WindowPlacement? WindowPlacement { get; set; }

    /// <summary>
    /// Transient signal used by the Settings window so an intentional location
    /// edit is distinguishable from unrelated autosaves.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LocationSettingsChanged { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public List<string>? LocationSettingsBaselineAdditionalWurmDataLocations { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public List<KnownLogLocation>? LocationSettingsBaselineKnownLogLocations { get; set; }
}
