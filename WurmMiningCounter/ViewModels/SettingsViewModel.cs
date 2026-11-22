using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private bool _alwaysOnTop;
    private bool _startMinimizedToTray;
    private bool _diagnosticsEnabled;
    private readonly List<string> _additionalWurmDataLocations;
    private readonly List<KnownLogLocationSettingItem> _knownLogLocations;
    private List<string> _locationSettingsBaselineAdditionalWurmDataLocations;
    private List<KnownLogLocation> _locationSettingsBaselineKnownLogLocations;
    private readonly RuntimeDiagnosticService? _diagnostics;
    private readonly Func<IEnumerable<KnownLogLocation>, IEnumerable<string>, WurmLogLocationResult> _locationDiscovery;
    private readonly LogDiscoverySnapshotService? _snapshotDiscovery;
    private readonly Action<Action>? _postToUi;
    internal LogDiscoverySnapshot? LastDiscoverySnapshot { get; private set; }
    private readonly ObservableCollection<string> _additionalWurmDataLocationItems;
    private readonly ObservableCollection<KnownLogLocationSettingItem> _knownLogLocationItems;
    private readonly ObservableCollection<DiscoveredLogRoot> _discoveredRoots = [];
    private readonly ObservableCollection<string> _locationDiscoveryDiagnostics = [];
    private CancellationTokenSource? _locationDiscoveryCancellation;
    private int _locationDiscoveryRunning;
    private int _locationDiscoveryVersion;
    private int _locationConfigurationVersion;
    private long _lastSnapshotVersion;
    private int _isDisposed;
    private bool _locationSettingsDirty;
    private string? _selectedAdditionalWurmDataLocation;
    private string _locationDiscoveryStatusText = "Locations have not been scanned.";
    private string _discoveryDiagnosticSummaryText = "No warnings or errors.";

    public SettingsViewModel(AppSettings settings, RuntimeDiagnosticService? diagnostics = null)
        : this(settings, diagnostics, (Func<IEnumerable<KnownLogLocation>, IEnumerable<string>, WurmLogLocationResult>?)null)
    {
    }

    internal SettingsViewModel(
        AppSettings settings,
        RuntimeDiagnosticService? diagnostics,
        Func<IEnumerable<KnownLogLocation>, IEnumerable<string>, WurmLogLocationResult>? locationDiscovery)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _alwaysOnTop = settings.AlwaysOnTop;
        _startMinimizedToTray = settings.StartMinimizedToTray;
        _diagnosticsEnabled = settings.DiagnosticsEnabled;
        _additionalWurmDataLocations = [.. settings.AdditionalWurmDataLocations];
        _locationSettingsBaselineAdditionalWurmDataLocations = [.. _additionalWurmDataLocations];
        _additionalWurmDataLocationItems = new ObservableCollection<string>(_additionalWurmDataLocations);
        AdditionalWurmDataLocations = new ReadOnlyObservableCollection<string>(_additionalWurmDataLocationItems);
        _knownLogLocations = global::WurmMiningCounter.Models.KnownLogLocations.CopyAll(settings.KnownLogLocations)
            .Select(location => new KnownLogLocationSettingItem(location, OnLocationSettingsChanged))
            .ToList();
        _locationSettingsBaselineKnownLogLocations = global::WurmMiningCounter.Models.KnownLogLocations.CopyAll(settings.KnownLogLocations);
        _knownLogLocationItems = new ObservableCollection<KnownLogLocationSettingItem>(_knownLogLocations);
        KnownLogLocations = new ReadOnlyObservableCollection<KnownLogLocationSettingItem>(_knownLogLocationItems);
        DiscoveredRoots = new ReadOnlyObservableCollection<DiscoveredLogRoot>(_discoveredRoots);
        LocationDiscoveryDiagnostics = new ReadOnlyObservableCollection<string>(_locationDiscoveryDiagnostics);
        _diagnostics = diagnostics;
        _locationDiscovery = locationDiscovery
            ?? ((knownLocations, additionalLocations) => new WurmLogLocationLocator().Locate(knownLocations, additionalLocations));
    }

    internal SettingsViewModel(
        AppSettings settings,
        RuntimeDiagnosticService? diagnostics,
        LogDiscoverySnapshotService snapshotDiscovery,
        Action<Action>? postToUi = null)
        : this(settings, diagnostics, (Func<IEnumerable<KnownLogLocation>, IEnumerable<string>, WurmLogLocationResult>?)null)
    {
        _snapshotDiscovery = snapshotDiscovery ?? throw new ArgumentNullException(nameof(snapshotDiscovery));
        _postToUi = postToUi;
        _snapshotDiscovery.SnapshotPublished += OnSnapshotPublished;
        PublishSnapshot(_snapshotDiscovery.GetCurrentSnapshot());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => SetProperty(ref _alwaysOnTop, value);
    }

    public bool StartMinimizedToTray
    {
        get => _startMinimizedToTray;
        set => SetProperty(ref _startMinimizedToTray, value);
    }

    public bool DiagnosticsEnabled
    {
        get => _diagnosticsEnabled;
        set
        {
            if (!SetProperty(ref _diagnosticsEnabled, value))
                return;

            OnPropertyChanged(nameof(DiagnosticsStatusText));
        }
    }

    public string DiagnosticsStatusText => DiagnosticsEnabled ? "Enabled" : "Disabled";

    public ReadOnlyObservableCollection<string> AdditionalWurmDataLocations { get; }

    public string? SelectedAdditionalWurmDataLocation
    {
        get => _selectedAdditionalWurmDataLocation;
        set
        {
            if (SetProperty(ref _selectedAdditionalWurmDataLocation, value))
                OnPropertyChanged(nameof(CanRemoveAdditionalWurmDataLocation));
        }
    }

    public bool CanRemoveAdditionalWurmDataLocation => SelectedAdditionalWurmDataLocation is not null;

    public ReadOnlyObservableCollection<KnownLogLocationSettingItem> KnownLogLocations { get; }

    public ReadOnlyObservableCollection<DiscoveredLogRoot> DiscoveredRoots { get; }

    public ReadOnlyObservableCollection<string> LocationDiscoveryDiagnostics { get; }

    public bool IsLocationDiscoveryRunning => Volatile.Read(ref _locationDiscoveryRunning) != 0;

    public bool CanFindLocations => !IsLocationDiscoveryRunning;

    public int DiscoveryRootCount => _discoveredRoots.Count;

    public int DiscoveryProfileCount => _discoveredRoots.Sum(root => root.Profiles.Count);

    public int DiscoveryEventFileCount => _discoveredRoots.Sum(root => root.EventFileCount);

    public int DiscoverySkillsFileCount => _discoveredRoots.Sum(root => root.SkillsFileCount);

    public string LocationDiscoveryStatusText
    {
        get => _locationDiscoveryStatusText;
        private set => SetProperty(ref _locationDiscoveryStatusText, value);
    }

    public string DiscoveryDiagnosticSummaryText
    {
        get => _discoveryDiagnosticSummaryText;
        private set => SetProperty(ref _discoveryDiagnosticSummaryText, value);
    }

    public int DiagnosticEntryCount { get; private set; }

    public string DiagnosticEntryCountText => $"Entries: {DiagnosticEntryCount} / {RuntimeDiagnosticService.MaximumEntries}";

    public void RefreshDiagnosticsState()
    {
        var entryCount = _diagnostics?.EntryCount ?? 0;
        if (DiagnosticEntryCount == entryCount)
            return;

        DiagnosticEntryCount = entryCount;
        OnPropertyChanged(nameof(DiagnosticEntryCount));
        OnPropertyChanged(nameof(DiagnosticEntryCountText));
    }

    public AppSettings CreateSettings()
    {
        return new AppSettings
        {
            AlwaysOnTop = AlwaysOnTop,
            StartMinimizedToTray = StartMinimizedToTray,
            DiagnosticsEnabled = DiagnosticsEnabled,
            AdditionalWurmDataLocations = [.. _additionalWurmDataLocations],
            KnownLogLocations = _knownLogLocations.Select(location => location.CreateLocation()).ToList(),
            LocationSettingsChanged = _locationSettingsDirty,
            LocationSettingsBaselineAdditionalWurmDataLocations = [.. _locationSettingsBaselineAdditionalWurmDataLocations],
            LocationSettingsBaselineKnownLogLocations = global::WurmMiningCounter.Models.KnownLogLocations.CopyAll(_locationSettingsBaselineKnownLogLocations)
        };
    }

    public void MarkLocationSettingsSaved()
    {
        _locationSettingsDirty = false;
        _locationSettingsBaselineAdditionalWurmDataLocations = [.. _additionalWurmDataLocations];
        _locationSettingsBaselineKnownLogLocations = _knownLogLocations.Select(location => location.CreateLocation()).ToList();
    }

    public bool AddAdditionalWurmDataLocation(string? path)
    {
        var normalizedPath = path?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPath)
            || _additionalWurmDataLocations.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _additionalWurmDataLocations.Add(normalizedPath);
        _additionalWurmDataLocationItems.Add(normalizedPath);
        OnLocationSettingsChanged();
        return true;
    }

    public bool RemoveAdditionalWurmDataLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var existing = _additionalWurmDataLocations.FirstOrDefault(candidate =>
            string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return false;

        _additionalWurmDataLocations.Remove(existing);
        _additionalWurmDataLocationItems.Remove(existing);
        if (string.Equals(SelectedAdditionalWurmDataLocation, existing, StringComparison.OrdinalIgnoreCase))
            SelectedAdditionalWurmDataLocation = null;
        OnLocationSettingsChanged();
        return true;
    }

    public bool RemoveSelectedAdditionalWurmDataLocation() =>
        RemoveAdditionalWurmDataLocation(SelectedAdditionalWurmDataLocation);

    public async Task FindLocationsAsync()
    {
        if (Interlocked.CompareExchange(ref _locationDiscoveryRunning, 1, 0) != 0)
            return;

        var version = Interlocked.Increment(ref _locationDiscoveryVersion);
        var configurationVersion = Volatile.Read(ref _locationConfigurationVersion);
        var cancellation = new CancellationTokenSource();
        _locationDiscoveryCancellation = cancellation;
        OnPropertyChanged(nameof(IsLocationDiscoveryRunning));
        OnPropertyChanged(nameof(CanFindLocations));
        LocationDiscoveryStatusText = "Finding log locations…";

        var knownLocations = _knownLogLocations.Select(location => location.CreateLocation()).ToArray();
        var additionalLocations = _additionalWurmDataLocations.ToArray();
        try
        {
            var snapshot = await Task.Run(() => _snapshotDiscovery?.Discover(knownLocations, additionalLocations));
            var result = snapshot?.Discovery ?? _locationDiscovery(knownLocations, additionalLocations);
            if (!cancellation.IsCancellationRequested
                && version == Volatile.Read(ref _locationDiscoveryVersion)
                && configurationVersion == Volatile.Read(ref _locationConfigurationVersion))
            {
                var publication = _snapshotDiscovery?.GetCurrentSnapshot();
                if (snapshot is not null && publication?.Snapshot == snapshot)
                    PublishSnapshot(publication);
                else
                {
                    LastDiscoverySnapshot = snapshot;
                    PublishLocationDiscovery(result);
                }
            }
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested
                && version == Volatile.Read(ref _locationDiscoveryVersion)
                && configurationVersion == Volatile.Read(ref _locationConfigurationVersion))
            {
                _locationDiscoveryDiagnostics.Clear();
                _locationDiscoveryDiagnostics.Add($"Discovery failed: {exception.Message}");
                DiscoveryDiagnosticSummaryText = "0 warnings, 1 error.";
                LocationDiscoveryStatusText = "Finding locations failed.";
            }
        }
        finally
        {
            if (ReferenceEquals(_locationDiscoveryCancellation, cancellation))
                _locationDiscoveryCancellation = null;
            cancellation.Dispose();
            if (version == Volatile.Read(ref _locationDiscoveryVersion))
            {
                Interlocked.Exchange(ref _locationDiscoveryRunning, 0);
                OnPropertyChanged(nameof(IsLocationDiscoveryRunning));
                OnPropertyChanged(nameof(CanFindLocations));
            }
        }
    }

    public void CancelLocationDiscovery()
    {
        Interlocked.Increment(ref _locationDiscoveryVersion);
        var cancellation = Interlocked.Exchange(ref _locationDiscoveryCancellation, null);
        cancellation?.Cancel();
        if (Interlocked.Exchange(ref _locationDiscoveryRunning, 0) != 0)
        {
            OnPropertyChanged(nameof(IsLocationDiscoveryRunning));
            OnPropertyChanged(nameof(CanFindLocations));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        if (_snapshotDiscovery is not null)
            _snapshotDiscovery.SnapshotPublished -= OnSnapshotPublished;
        CancelLocationDiscovery();
    }

    private void OnSnapshotPublished(object? sender, LogDiscoverySnapshotPublication publication)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            return;

        var configurationVersion = Volatile.Read(ref _locationConfigurationVersion);

        void Apply()
        {
            if (Volatile.Read(ref _isDisposed) == 0
                && !IsLocationDiscoveryRunning
                && configurationVersion == Volatile.Read(ref _locationConfigurationVersion))
            {
                PublishSnapshot(publication);
            }
        }

        if (_postToUi is null)
            Apply();
        else
            _postToUi(Apply);
    }

    private void PublishSnapshot(LogDiscoverySnapshotPublication? publication)
    {
        if (publication is null || publication.Version <= _lastSnapshotVersion)
            return;

        _lastSnapshotVersion = publication.Version;
        LastDiscoverySnapshot = publication.Snapshot;
        PublishLocationDiscovery(publication.Snapshot.Discovery);
    }

    private void PublishLocationDiscovery(WurmLogLocationResult result)
    {
        _discoveredRoots.Clear();
        foreach (var root in result.DataRoots)
            _discoveredRoots.Add(new DiscoveredLogRoot(root));

        foreach (var location in _knownLogLocations)
        {
            var detected = result.DataRoots
                .Where(root => root.Origins.Any(origin => string.Equals(origin.RuleId, location.Id, StringComparison.Ordinal)))
                .Select(root => root.PlayersDirectoryPath);
            // An incomplete scan cannot establish that a previously detected
            // rule disappeared. Retain that evidence until a complete scan.
            if (!result.IsComplete)
                detected = detected.Concat(location.DetectedPlayersDirectories);
            location.SetDetectedPlayersDirectories(detected);
        }

        _locationDiscoveryDiagnostics.Clear();
        foreach (var diagnostic in result.SteamDiagnostics)
            _locationDiscoveryDiagnostics.Add($"Steam: {diagnostic.Issue} - {diagnostic.Path}");
        foreach (var rejection in result.Rejections)
            _locationDiscoveryDiagnostics.Add(rejection.Reason == WurmLogLocationRejectionReason.ProfileLogsFolderMissing
                ? "Profile logs folder missing"
                : $"Location: {rejection.Reason}");
        foreach (var conflict in result.ProfileNameConflicts)
            _locationDiscoveryDiagnostics.Add($"Profile name conflict: {conflict.ProfileName} ({conflict.Sources.Count} sources)");
        foreach (var ambiguity in result.SourceOwnershipAmbiguities)
            _locationDiscoveryDiagnostics.Add($"Source ownership ambiguity ({ambiguity.Claims.Count} claims)");
        foreach (var uncertainty in result.Uncertainties)
            _locationDiscoveryDiagnostics.Add($"Physical identity unavailable - {uncertainty.DirectoryPath}");

        var errorCount = result.SteamDiagnostics.Count(item => item.Issue is SteamDiscoveryIssue.AccessDenied or SteamDiscoveryIssue.IoFailure or SteamDiscoveryIssue.MetadataInvalid)
            + result.Rejections.Count(item => item.Reason is WurmLogLocationRejectionReason.AccessDenied or WurmLogLocationRejectionReason.IoFailure or WurmLogLocationRejectionReason.InvalidStructure)
            + result.SourceOwnershipAmbiguities.Count;
        var warningCount = result.SteamDiagnostics.Count(item => item.Issue is SteamDiscoveryIssue.SteamNotFound or SteamDiscoveryIssue.LibraryUnavailable)
            + result.Rejections.Count(item => item.Reason is WurmLogLocationRejectionReason.Missing
                or WurmLogLocationRejectionReason.ProfileLogsFolderMissing)
            + result.ProfileNameConflicts.Count
            + result.Uncertainties.Count;
        DiscoveryDiagnosticSummaryText = warningCount == 0 && errorCount == 0
            ? "No warnings or errors."
            : $"{warningCount} {(warningCount == 1 ? "warning" : "warnings")}, {errorCount} {(errorCount == 1 ? "error" : "errors")}.";

        RefreshDiscoveryCounts();
        LocationDiscoveryStatusText = result.IsComplete
            ? "Discovery completed."
            : "Discovery incomplete; showing verified partial results.";
    }

    private void OnLocationSettingsChanged()
    {
        _locationSettingsDirty = true;
        Interlocked.Increment(ref _locationConfigurationVersion);
        var currentSnapshot = _snapshotDiscovery?.GetCurrentSnapshot();
        if (currentSnapshot is not null)
            _lastSnapshotVersion = Math.Max(_lastSnapshotVersion, currentSnapshot.Version);
        _discoveredRoots.Clear();
        _locationDiscoveryDiagnostics.Clear();
        DiscoveryDiagnosticSummaryText = "No warnings or errors.";
        foreach (var location in _knownLogLocations)
            location.SetDetectedPlayersDirectories([]);
        RefreshDiscoveryCounts();
        LocationDiscoveryStatusText = "Location settings changed. Run Find locations to refresh.";
        OnPropertyChanged(nameof(LocationSettingsChanged));
    }

    private void RefreshDiscoveryCounts()
    {
        OnPropertyChanged(nameof(DiscoveryRootCount));
        OnPropertyChanged(nameof(DiscoveryProfileCount));
        OnPropertyChanged(nameof(DiscoveryEventFileCount));
        OnPropertyChanged(nameof(DiscoverySkillsFileCount));
    }

    // A dedicated notification lets Settings persistence remain transactional while
    // the individual location controls stay independent from general settings.
    public bool LocationSettingsChanged => false;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class KnownLogLocationSettingItem : INotifyPropertyChanged
{
    private readonly KnownLogLocation _location;
    private readonly Action _changed;
    private IReadOnlyList<string> _detectedPlayersDirectories = [];

    internal KnownLogLocationSettingItem(KnownLogLocation location, Action changed)
    {
        _location = KnownLogLocations.Copy(location);
        _changed = changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => _location.Id;
    public string DisplayName => _location.Id switch
    {
        "wurm-online-standard" => "Wurm Online Standard",
        "wurm-online-steam" => "Wurm Online Steam",
        "wurm-unlimited-steam" => "Wurm Unlimited Steam",
        _ => _location.Game
    };
    public string Game => _location.Game;
    public string Type => _location.Type;
    public string Details => string.Equals(_location.Type, KnownLogLocations.SteamType, StringComparison.OrdinalIgnoreCase)
        ? $"Steam AppID {_location.AppId}: {_location.PlayersRelativePath}"
        : _location.Path ?? string.Empty;
    public string RuleDescription => string.Equals(_location.Type, KnownLogLocations.SteamType, StringComparison.OrdinalIgnoreCase)
        ? $"Steam rule - AppID {_location.AppId}"
        : "Standard client folder rule";
    public IReadOnlyList<string> DetectedPlayersDirectories => _detectedPlayersDirectories;
    public bool HasDetectedPlayersDirectories => _detectedPlayersDirectories.Count > 0;
    public string DiscoveryStatus => !Enabled
        ? "Disabled"
        : HasDetectedPlayersDirectories
            ? "Detected"
            : "Not detected";

    public bool Enabled
    {
        get => _location.Enabled;
        set
        {
            if (_location.Enabled == value)
                return;

            _location.Enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiscoveryStatus)));
            _changed();
        }
    }

    internal void SetDetectedPlayersDirectories(IEnumerable<string> paths)
    {
        _detectedPlayersDirectories = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetectedPlayersDirectories)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDetectedPlayersDirectories)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiscoveryStatus)));
    }

    internal KnownLogLocation CreateLocation() => KnownLogLocations.Copy(_location);
}

public sealed class DiscoveredLogRoot
{
    public DiscoveredLogRoot(WurmLogDataRoot root)
    {
        PlayersDirectoryPath = root.PlayersDirectoryPath;
        InputCount = root.InputPaths.Count;
        Origins = root.Origins.Select(origin => new DiscoveredLogOrigin(origin)).ToArray();
        SourceSummary = string.Join(" · ", Origins.Select(origin => origin.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(SourceSummary))
            SourceSummary = "Unknown source";
        Profiles = root.Profiles.Select(profile => new DiscoveredLogProfile(profile)).ToArray();
    }

    public string PlayersDirectoryPath { get; }
    public int InputCount { get; }
    public string SourceSummary { get; }
    public IReadOnlyList<DiscoveredLogOrigin> Origins { get; }
    public IReadOnlyList<DiscoveredLogProfile> Profiles { get; }
    public int ProfileCount => Profiles.Count;
    public int EventFileCount => Profiles.Sum(profile => profile.EventFileCount);
    public int SkillsFileCount => Profiles.Sum(profile => profile.SkillsFileCount);
}

public sealed class DiscoveredLogOrigin
{
    public DiscoveredLogOrigin(WurmLogLocationOrigin origin)
    {
        RuleId = origin.RuleId;
        Game = origin.Game;
        IsManual = origin.IsManual;
        InputPath = origin.InputPath;
    }

    public string? RuleId { get; }
    public string? Game { get; }
    public bool IsManual { get; }
    public string InputPath { get; }
    public string DisplayName => IsManual
        ? "Manual location"
        : string.IsNullOrWhiteSpace(Game)
            ? "Unknown location"
            : RuleId switch
            {
                "wurm-online-standard" => "Wurm Online Standard",
                "wurm-online-steam" => "Wurm Online Steam",
                "wurm-unlimited-steam" => "Wurm Unlimited Steam",
                _ => string.IsNullOrWhiteSpace(RuleId) ? Game : $"{Game} ({RuleId})"
            };
}

public sealed class DiscoveredLogProfile
{
    public DiscoveredLogProfile(WurmLogProfileLocation profile)
    {
        ProfileName = profile.ProfileName;
        LogsDirectoryPath = profile.LogsDirectoryPath;
        EventFileCount = profile.Files.Count(file => file.Kind == WurmLogFileKind.Event);
        SkillsFileCount = profile.Files.Count(file => file.Kind == WurmLogFileKind.Skills);
    }

    public string ProfileName { get; }
    public string LogsDirectoryPath { get; }
    public int EventFileCount { get; }
    public int SkillsFileCount { get; }
}
