using System.IO;
using System.Text.RegularExpressions;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public enum WurmLogLocationRejectionReason
{
    Missing,
    ProfileLogsFolderMissing,
    AccessDenied,
    InvalidStructure,
    IoFailure
}

internal enum WurmLogDirectoryProbeResult
{
    Exists,
    Missing,
    AccessDenied,
    IoFailure
}

public sealed record WurmLogLocationRejection(
    string InputPath,
    WurmLogLocationRejectionReason Reason);

public sealed record WurmLogProfileLocation(
    string ProfileName,
    string LogsDirectoryPath)
{
    public string LogicalLogsDirectoryPath { get; init; } = LogsDirectoryPath;
    internal string ProfileDirectoryPath { get; init; } = Path.GetDirectoryName(LogsDirectoryPath) ?? LogsDirectoryPath;
    // Opaque FileId evidence for the physical profile directory. A missing
    // value is unknown evidence and must never be replaced with a path key.
    internal string? PhysicalDirectoryKey { get; init; }
    internal string? PhysicalLogsDirectoryKey { get; init; }
    public IReadOnlyList<WurmLogFileLocation> Files { get; init; } = [];
}

public enum WurmLogFileKind { Event, Skills }

public sealed record WurmLogFileLocation(
    WurmLogFileKind Kind,
    string FilePath,
    string PlayersDirectoryPath,
    string ProfileName,
    string LogsDirectoryPath);

public sealed record WurmLogLocationOrigin(
    string InputPath,
    string? RuleId,
    string? Game,
    bool IsManual);

public sealed record WurmLogDataRoot(
    string PlayersDirectoryPath,
    IReadOnlyList<WurmLogProfileLocation> Profiles)
{
    public IReadOnlyList<string> InputPaths { get; init; } = [];
    public IReadOnlyList<WurmLogLocationOrigin> Origins { get; init; } = [];
    public bool IsComplete { get; init; } = true;
}

public sealed record WurmLogSourceClaim(string PlayersDirectoryPath, WurmLogProfileLocation Profile);

public sealed record WurmLogSourceOwnershipAmbiguity(IReadOnlyList<WurmLogSourceClaim> Claims);

public sealed record WurmLogProfileNameConflict(
    string ProfileName,
    IReadOnlyList<WurmLogProfileLocation> Sources);

public enum WurmLogLocationUncertaintyReason
{
    PhysicalIdentityUnavailable
}

public sealed record WurmLogLocationUncertainty(
    string DirectoryPath,
    WurmLogLocationUncertaintyReason Reason);

public sealed record WurmLogLocationResult(
    IReadOnlyList<WurmLogDataRoot> DataRoots,
    IReadOnlyList<WurmLogLocationRejection> Rejections,
    IReadOnlyList<WurmLogProfileNameConflict> ProfileNameConflicts,
    IReadOnlyList<SteamDiscoveryDiagnostic> SteamDiagnostics,
    IReadOnlyList<WurmLogLocationUncertainty> Uncertainties)
{
    public IReadOnlyList<WurmLogSourceOwnershipAmbiguity> SourceOwnershipAmbiguities { get; init; } = [];
    public IReadOnlyList<WurmLogFileLocation> Files { get; init; } = [];
    public bool IsComplete { get; init; } = true;
}

/// <summary>
/// Resolves only user-provided Wurm data locations. It never searches disks or
/// treats a game installation as a data directory.
/// </summary>
public sealed class WurmLogLocationLocator
{
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, IEnumerable<string>> _enumerateDirectories;
    private readonly Func<string, IEnumerable<string>> _enumerateFiles;
    private readonly Func<string, WurmLogDirectoryProbeResult> _probeDirectory;
    private readonly Func<string, string> _canonicalizeDirectory;
    private readonly IDirectoryPhysicalIdentityProvider _directoryIdentityProvider;
    private readonly Func<string> _defaultPlayersDirectoryPathProvider;
    private readonly Func<IReadOnlyCollection<int>, SteamInstallationDiscoveryResult> _steamDiscovery;

    public WurmLogLocationLocator()
        : this(Directory.Exists, Directory.EnumerateDirectories, WurmDiscoveryPathPolicy.CanonicalizeDirectory,
            new WindowsDirectoryPhysicalIdentityProvider(), GetDefaultPlayersDirectoryPath,
            appId => new SteamInstallationLocator().Locate(appId),
            appIds => new SteamInstallationLocator().LocateMany(appIds), null, ProbeDirectory)
    {
    }

    internal WurmLogLocationLocator(
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateDirectories,
        Func<string, string> canonicalizeDirectory)
        : this(directoryExists, enumerateDirectories, canonicalizeDirectory, _ => null, GetDefaultPlayersDirectoryPath,
            appId => new SteamInstallationLocator().Locate(appId),
            appIds => new SteamInstallationLocator().LocateMany(appIds))
    {
    }

    internal WurmLogLocationLocator(
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateDirectories,
        Func<string, string> canonicalizeDirectory,
        Func<string> defaultPlayersDirectoryPathProvider)
        : this(directoryExists, enumerateDirectories, canonicalizeDirectory, _ => null, defaultPlayersDirectoryPathProvider,
            appId => new SteamInstallationLocator().Locate(appId),
            appIds => new SteamInstallationLocator().LocateMany(appIds))
    {
    }

    internal WurmLogLocationLocator(
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateDirectories,
        Func<string, string> canonicalizeDirectory,
        Func<string> defaultPlayersDirectoryPathProvider,
        Func<int, IReadOnlyList<string>> steamInstallations)
        : this(directoryExists, enumerateDirectories, canonicalizeDirectory, _ => null, defaultPlayersDirectoryPathProvider,
            steamInstallations,
            appIds => new SteamInstallationDiscoveryResult(
                appIds.Distinct().ToDictionary(appId => appId, appId => steamInstallations(appId)), []))
    {
    }

    internal WurmLogLocationLocator(
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateDirectories,
        Func<string, string> canonicalizeDirectory,
        Func<string> defaultPlayersDirectoryPathProvider,
        Func<int, IReadOnlyList<string>> steamInstallations,
        Func<IReadOnlyCollection<int>, SteamInstallationDiscoveryResult> steamDiscovery)
        : this(directoryExists, enumerateDirectories, canonicalizeDirectory, _ => null, defaultPlayersDirectoryPathProvider,
            steamInstallations, steamDiscovery)
    {
    }

    internal WurmLogLocationLocator(
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateDirectories,
        Func<string, string> canonicalizeDirectory,
        Func<string, string?> getDirectoryIdentity,
        Func<string> defaultPlayersDirectoryPathProvider,
        Func<int, IReadOnlyList<string>> steamInstallations,
        Func<IReadOnlyCollection<int>, SteamInstallationDiscoveryResult> steamDiscovery)
        : this(directoryExists, enumerateDirectories, canonicalizeDirectory,
            new DelegateDirectoryPhysicalIdentityProvider(getDirectoryIdentity ?? throw new ArgumentNullException(nameof(getDirectoryIdentity))),
            defaultPlayersDirectoryPathProvider, steamInstallations, steamDiscovery)
    {
    }

    internal WurmLogLocationLocator(
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateDirectories,
        Func<string, string> canonicalizeDirectory,
        IDirectoryPhysicalIdentityProvider directoryIdentityProvider,
        Func<string> defaultPlayersDirectoryPathProvider,
        Func<int, IReadOnlyList<string>> steamInstallations,
        Func<IReadOnlyCollection<int>, SteamInstallationDiscoveryResult> steamDiscovery)
        : this(directoryExists, enumerateDirectories, canonicalizeDirectory, directoryIdentityProvider,
            defaultPlayersDirectoryPathProvider, steamInstallations, steamDiscovery, null)
    {
    }

    internal WurmLogLocationLocator(
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateDirectories,
        Func<string, string> canonicalizeDirectory,
        IDirectoryPhysicalIdentityProvider directoryIdentityProvider,
        Func<string> defaultPlayersDirectoryPathProvider,
        Func<int, IReadOnlyList<string>> steamInstallations,
        Func<IReadOnlyCollection<int>, SteamInstallationDiscoveryResult> steamDiscovery,
        Func<string, IEnumerable<string>>? enumerateFiles,
        Func<string, WurmLogDirectoryProbeResult>? probeDirectory = null)
    {
        _directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        _enumerateDirectories = enumerateDirectories ?? throw new ArgumentNullException(nameof(enumerateDirectories));
        _enumerateFiles = enumerateFiles ?? (path => Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly));
        _probeDirectory = probeDirectory ?? (path => _directoryExists(path)
            ? WurmLogDirectoryProbeResult.Exists
            : WurmLogDirectoryProbeResult.Missing);
        _canonicalizeDirectory = canonicalizeDirectory ?? throw new ArgumentNullException(nameof(canonicalizeDirectory));
        _directoryIdentityProvider = directoryIdentityProvider ?? throw new ArgumentNullException(nameof(directoryIdentityProvider));
        _defaultPlayersDirectoryPathProvider = defaultPlayersDirectoryPathProvider ?? throw new ArgumentNullException(nameof(defaultPlayersDirectoryPathProvider));
        ArgumentNullException.ThrowIfNull(steamInstallations);
        _steamDiscovery = steamDiscovery ?? throw new ArgumentNullException(nameof(steamDiscovery));
    }

    public WurmLogLocationResult Locate(IEnumerable<string>? additionalLocations)
    {
        return Locate(additionalLocations, includeDefaultLocation: true);
    }

    public WurmLogLocationResult Locate(
        IEnumerable<KnownLogLocation>? knownLocations,
        IEnumerable<string>? additionalLocations = null)
    {
        var inputs = new List<LocationInput>();
        var rejections = new List<WurmLogLocationRejection>();
        var validLocations = new List<KnownLogLocation>();
        foreach (var location in knownLocations ?? [])
        {
            if (!KnownLogLocations.TryValidate(location, out _))
            {
                rejections.Add(new WurmLogLocationRejection(location?.Id ?? string.Empty, WurmLogLocationRejectionReason.InvalidStructure));
                continue;
            }
            if (!location!.Enabled)
                continue;

            validLocations.Add(location);
        }

        var steamAppIds = validLocations
            .Where(location => string.Equals(location.Type, KnownLogLocations.SteamType, StringComparison.OrdinalIgnoreCase))
            .Select(location => location.AppId!.Value)
            .Distinct()
            .ToArray();
        var steamResult = steamAppIds.Length == 0
            ? new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), [])
            : _steamDiscovery(steamAppIds);

        foreach (var location in validLocations)
        {

            if (string.Equals(location.Type, KnownLogLocations.DirectType, StringComparison.OrdinalIgnoreCase))
                inputs.Add(new(location.Path!, location.Id, location.Game, false));
            else
            {
                foreach (var installation in steamResult.InstallationsByAppId.GetValueOrDefault(location.AppId!.Value, []))
                {
                    if (KnownLogLocations.TryResolveRelativePath(
                        installation, location.PlayersRelativePath!, out var playersPath))
                    {
                        inputs.Add(new(playersPath, location.Id, location.Game, false));
                    }
                    else
                    {
                        rejections.Add(new WurmLogLocationRejection(location.Id ?? string.Empty, WurmLogLocationRejectionReason.InvalidStructure));
                    }
                }
            }
        }
        if (additionalLocations is not null)
            inputs.AddRange(additionalLocations.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => new LocationInput(path, null, null, true)));

        return LocateInputs(inputs, rejections, steamResult.Diagnostics);
    }

    internal WurmLogLocationResult Locate(
        IEnumerable<string>? additionalLocations,
        bool includeDefaultLocation)
    {
        var inputs = new List<LocationInput>();
        if (includeDefaultLocation)
            inputs.Add(new(_defaultPlayersDirectoryPathProvider(), "wurm-online-standard", "WurmOnline", false));
        if (additionalLocations is not null)
            inputs.AddRange(additionalLocations.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => new LocationInput(path, null, null, true)));

        return LocateInputs(inputs, [], []);
    }

    private WurmLogLocationResult LocateInputs(
        IEnumerable<LocationInput> inputs,
        List<WurmLogLocationRejection> rejections,
        IReadOnlyList<SteamDiscoveryDiagnostic> steamDiagnostics)
    {
        var rootCandidates = new Dictionary<string, RootCandidate>(WurmDiscoveryPathPolicy.Comparer);
        var uncertainties = new List<WurmLogLocationUncertainty>();
        var directoryIdentities = new Dictionary<string, string?>(WurmDiscoveryPathPolicy.Comparer);
        foreach (var input in inputs)
        {
            if (!TryResolvePlayersDirectory(input.Path, directoryIdentities, out var playersDirectory, out var rejectionReason))
            {
                rejections.Add(new WurmLogLocationRejection(input.Path, rejectionReason));
                continue;
            }

            var rootKey = GetDeduplicationKey(playersDirectory);
            if (rootCandidates.TryGetValue(rootKey, out var existingRoot))
            {
                existingRoot.Origins.Add(input.ToOrigin());
                continue;
            }

            if (playersDirectory.PhysicalIdentity is null)
                uncertainties.Add(new WurmLogLocationUncertainty(playersDirectory.CanonicalPath, WurmLogLocationUncertaintyReason.PhysicalIdentityUnavailable));

            if (!TryGetProfiles(playersDirectory, directoryIdentities, rejections, out var profiles, out var profilesComplete, out rejectionReason))
            {
                rejections.Add(new WurmLogLocationRejection(input.Path, rejectionReason));
                continue;
            }

            rootCandidates.Add(rootKey, new RootCandidate(playersDirectory.CanonicalPath, profiles, [input.ToOrigin()], profilesComplete));
        }

        var acceptedProfiles = rootCandidates.Values.ToDictionary(root => root.PlayersDirectoryPath,
            _ => new List<WurmLogProfileLocation>(), WurmDiscoveryPathPolicy.Comparer);
        var ambiguities = new List<WurmLogSourceOwnershipAmbiguity>();
        var sourceGroups = rootCandidates.Values
            .SelectMany(root => root.Profiles.Select(profile => new { Root = root, Profile = profile }))
            .GroupBy(candidate => GetDeduplicationKey(candidate.Profile.LogsDirectory), WurmDiscoveryPathPolicy.Comparer);
        foreach (var group in sourceGroups)
        {
            var candidates = group.OrderBy(candidate => candidate.Root.PlayersDirectoryPath, WurmDiscoveryPathPolicy.Comparer)
                .ThenBy(candidate => candidate.Profile.Location.LogicalLogsDirectoryPath, WurmDiscoveryPathPolicy.Comparer).ToArray();
            foreach (var candidate in candidates.Where(candidate => candidate.Profile.LogsDirectory.PhysicalIdentity is null))
                uncertainties.Add(new(candidate.Profile.Location.LogsDirectoryPath, WurmLogLocationUncertaintyReason.PhysicalIdentityUnavailable));

            if (candidates.Select(candidate => candidate.Profile.Location.ProfileName).Distinct(WurmDiscoveryPathPolicy.Comparer).Skip(1).Any())
            {
                ambiguities.Add(new(candidates.Select(candidate => new WurmLogSourceClaim(candidate.Root.PlayersDirectoryPath, candidate.Profile.Location)).ToArray()));
                continue;
            }

            var representative = candidates[0];
            acceptedProfiles[representative.Root.PlayersDirectoryPath].Add(representative.Profile.Location);
        }

        var roots = rootCandidates.Values.Select(root => new WurmLogDataRoot(root.PlayersDirectoryPath,
            acceptedProfiles[root.PlayersDirectoryPath].OrderBy(profile => profile.ProfileName, WurmDiscoveryPathPolicy.Comparer)
                .ThenBy(profile => profile.LogsDirectoryPath, WurmDiscoveryPathPolicy.Comparer).ToArray())
            {
                InputPaths = root.Origins.Select(origin => origin.InputPath).ToArray(),
                Origins = root.Origins.ToArray(),
                IsComplete = root.IsComplete
            }).ToArray();

        roots = roots.Select(root => root with
        {
            Profiles = CatalogRootProfiles(root, rejections, out var catalogComplete),
            IsComplete = root.IsComplete && catalogComplete
        }).ToArray();

        var conflicts = roots
            .SelectMany(root => root.Profiles)
            .GroupBy(profile => profile.ProfileName, WurmDiscoveryPathPolicy.Comparer)
            .Where(group => group.Select(profile => profile.LogsDirectoryPath)
                .Distinct(WurmDiscoveryPathPolicy.Comparer)
                .Skip(1)
                .Any())
            .OrderBy(group => group.Key, WurmDiscoveryPathPolicy.Comparer)
            .Select(group => new WurmLogProfileNameConflict(
                group.Key,
                group.OrderBy(profile => profile.LogsDirectoryPath, WurmDiscoveryPathPolicy.Comparer).ToArray()))
            .ToArray();

        return new WurmLogLocationResult(
            roots.OrderBy(root => root.PlayersDirectoryPath, WurmDiscoveryPathPolicy.Comparer).ToArray(),
            rejections,
            conflicts,
            steamDiagnostics,
            uncertainties
                .DistinctBy(item => item.DirectoryPath, WurmDiscoveryPathPolicy.Comparer)
                .OrderBy(item => item.DirectoryPath, WurmDiscoveryPathPolicy.Comparer)
                .ToArray())
        {
            SourceOwnershipAmbiguities = ambiguities.ToArray(),
            Files = roots.SelectMany(root => root.Profiles).SelectMany(profile => profile.Files)
                .OrderBy(file => file.FilePath, WurmDiscoveryPathPolicy.Comparer).ToArray(),
            // A rejected root is not represented in roots. Do not let an empty
            // (or otherwise healthy) accepted set turn an access/I/O failure
            // into an authoritative complete scan.
            IsComplete = roots.All(root => root.IsComplete)
                && !rejections.Any(rejection => rejection.Reason is WurmLogLocationRejectionReason.AccessDenied
                    or WurmLogLocationRejectionReason.IoFailure)
                && !steamDiagnostics.Any(diagnostic => diagnostic.Issue is SteamDiscoveryIssue.AccessDenied
                    or SteamDiscoveryIssue.IoFailure)
        };
    }

    internal static string GetDefaultPlayersDirectoryPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "wurm", "players");

    private bool TryResolvePlayersDirectory(
        string inputPath,
        IDictionary<string, string?> directoryIdentities,
        out ResolvedDirectory playersDirectory,
        out WurmLogLocationRejectionReason rejectionReason)
    {
        playersDirectory = default;
        rejectionReason = WurmLogLocationRejectionReason.InvalidStructure;
        try
        {
            var inputDirectory = WurmDiscoveryPathPolicy.NormalizeDirectoryPath(inputPath);
            if (!TryProbeDirectory(inputDirectory, out rejectionReason))
            {
                return false;
            }

            string logicalPlayersDirectory;
            if (WurmDiscoveryPathPolicy.NameIs(inputDirectory, "players"))
                logicalPlayersDirectory = inputDirectory;

            else if (WurmDiscoveryPathPolicy.NameIs(inputDirectory, "logs"))
            {
                var profileDirectoryPath = Directory.GetParent(inputDirectory)?.FullName;
                var rootDirectoryPath = profileDirectoryPath is null ? null : Directory.GetParent(profileDirectoryPath)?.FullName;
                if (rootDirectoryPath is not null && WurmDiscoveryPathPolicy.NameIs(rootDirectoryPath, "players"))
                {
                    logicalPlayersDirectory = rootDirectoryPath;
                }
                else
                    return false;
            }
            else
            {
                var playersChildPath = Path.Combine(inputDirectory, "players");
                if (!TryProbeDirectory(playersChildPath, out rejectionReason))
                {
                    if (rejectionReason == WurmLogLocationRejectionReason.Missing)
                        rejectionReason = WurmLogLocationRejectionReason.InvalidStructure;
                    return false;
                }
                logicalPlayersDirectory = playersChildPath;
            }

            var canonicalPath = WurmDiscoveryPathPolicy.NormalizeDirectoryPath(_canonicalizeDirectory(logicalPlayersDirectory));
            if (!TryProbeDirectory(canonicalPath, out rejectionReason))
            {
                return false;
            }

            playersDirectory = new ResolvedDirectory(canonicalPath, GetDirectoryIdentity(canonicalPath, directoryIdentities), logicalPlayersDirectory);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            rejectionReason = WurmLogLocationRejectionReason.AccessDenied;
            return false;
        }
        catch (IOException)
        {
            rejectionReason = WurmLogLocationRejectionReason.IoFailure;
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private bool TryGetProfiles(
        ResolvedDirectory playersDirectory,
        IDictionary<string, string?> directoryIdentities,
        ICollection<WurmLogLocationRejection> rejections,
        out IReadOnlyList<ProfileCandidate> profiles,
        out bool isComplete,
        out WurmLogLocationRejectionReason rejectionReason)
    {
        var candidates = new List<ProfileCandidate>();
        isComplete = true;
        try
        {
            foreach (var profileDirectoryPath in _enumerateDirectories(playersDirectory.CanonicalPath))
            {
                try
                {
                    var profileName = Path.GetFileName(WurmDiscoveryPathPolicy.NormalizeDirectoryPath(profileDirectoryPath));
                    var canonicalProfilePath = WurmDiscoveryPathPolicy.NormalizeDirectoryPath(_canonicalizeDirectory(profileDirectoryPath));
                    var logicalLogsPath = Path.Combine(playersDirectory.LogicalPath ?? playersDirectory.CanonicalPath, profileName, "logs");
                    var logsInputPath = Path.Combine(profileDirectoryPath, "logs");
                    if (string.IsNullOrWhiteSpace(profileName))
                        continue;
                    if (!TryProbeDirectory(logsInputPath, out var logsRejectionReason))
                    {
                        isComplete = false;
                        if (logsRejectionReason == WurmLogLocationRejectionReason.Missing)
                            logsRejectionReason = WurmLogLocationRejectionReason.ProfileLogsFolderMissing;
                        rejections.Add(new WurmLogLocationRejection(profileDirectoryPath, logsRejectionReason));
                        continue;
                    }
                    var canonicalLogsPath = WurmDiscoveryPathPolicy.NormalizeDirectoryPath(_canonicalizeDirectory(logsInputPath));
                    var logsDirectory = new ResolvedDirectory(canonicalLogsPath, GetDirectoryIdentity(canonicalLogsPath, directoryIdentities));
                    candidates.Add(new ProfileCandidate(new WurmLogProfileLocation(profileName, canonicalLogsPath)
                    {
                        LogicalLogsDirectoryPath = logicalLogsPath,
                        ProfileDirectoryPath = canonicalProfilePath,
                        PhysicalLogsDirectoryKey = logsDirectory.PhysicalIdentity,
                        PhysicalDirectoryKey = GetDirectoryIdentity(canonicalProfilePath, directoryIdentities)
                    }, logsDirectory));
                }
                catch (UnauthorizedAccessException)
                {
                    isComplete = false;
                    rejections.Add(new WurmLogLocationRejection(profileDirectoryPath, WurmLogLocationRejectionReason.AccessDenied));
                }
                catch (IOException)
                {
                    isComplete = false;
                    rejections.Add(new WurmLogLocationRejection(profileDirectoryPath, WurmLogLocationRejectionReason.IoFailure));
                }
                catch (ArgumentException)
                {
                    isComplete = false;
                    rejections.Add(new WurmLogLocationRejection(profileDirectoryPath, WurmLogLocationRejectionReason.InvalidStructure));
                }
                catch (NotSupportedException)
                {
                    isComplete = false;
                    rejections.Add(new WurmLogLocationRejection(profileDirectoryPath, WurmLogLocationRejectionReason.InvalidStructure));
                }
            }
            profiles = candidates;
            rejectionReason = default;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            rejections.Add(new WurmLogLocationRejection(playersDirectory.CanonicalPath, WurmLogLocationRejectionReason.AccessDenied));
            profiles = candidates;
            isComplete = false;
            rejectionReason = default;
            return true;
        }
        catch (IOException)
        {
            rejections.Add(new WurmLogLocationRejection(playersDirectory.CanonicalPath, WurmLogLocationRejectionReason.IoFailure));
            profiles = candidates;
            isComplete = false;
            rejectionReason = default;
            return true;
        }
        catch (ArgumentException)
        {
            profiles = [];
            isComplete = false;
            rejectionReason = WurmLogLocationRejectionReason.InvalidStructure;
            return false;
        }
    }

    private string? GetDirectoryIdentity(string path, IDictionary<string, string?> directoryIdentities)
    {
        if (directoryIdentities.TryGetValue(path, out var identity))
            return identity;

        identity = TryGetDirectoryIdentity(path);
        directoryIdentities.Add(path, identity);
        return identity;
    }

    private string? TryGetDirectoryIdentity(string path)
    {
        try
        {
            return _directoryIdentityProvider.GetIdentity(path);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string GetDeduplicationKey(ResolvedDirectory directory) => directory.PhysicalIdentity is { Length: > 0 } identity
        ? $"id:{identity}"
        : $"path:{directory.CanonicalPath}";

    private IReadOnlyList<WurmLogFileLocation> CatalogFiles(
        string playersDirectoryPath,
        WurmLogProfileLocation profile,
        ICollection<WurmLogLocationRejection> rejections)
    {
        return CatalogFiles(playersDirectoryPath, profile, rejections, out _);
    }

    private IReadOnlyList<WurmLogProfileLocation> CatalogRootProfiles(
        WurmLogDataRoot root,
        ICollection<WurmLogLocationRejection> rejections,
        out bool isComplete)
    {
        isComplete = true;
        var profiles = new List<WurmLogProfileLocation>();
        foreach (var profile in root.Profiles)
        {
            var files = CatalogFiles(root.PlayersDirectoryPath, profile, rejections, out var profileComplete);
            isComplete &= profileComplete;
            profiles.Add(profile with { Files = files });
        }
        return profiles;
    }

    private IReadOnlyList<WurmLogFileLocation> CatalogFiles(
        string playersDirectoryPath,
        WurmLogProfileLocation profile,
        ICollection<WurmLogLocationRejection> rejections,
        out bool isComplete)
    {
        isComplete = true;
        var files = new List<WurmLogFileLocation>();
        try
        {
            foreach (var filePath in _enumerateFiles(profile.LogsDirectoryPath))
            {
                try
                {
                    var fileName = Path.GetFileName(filePath);
                    var kind = EventLogFileNameMatcher.IsMatch(fileName)
                        ? WurmLogFileKind.Event
                        : SkillsFileNamePattern.IsMatch(fileName)
                            ? WurmLogFileKind.Skills
                            : (WurmLogFileKind?)null;
                    if (kind is not null)
                        files.Add(new WurmLogFileLocation(kind.Value, Path.GetFullPath(filePath), playersDirectoryPath,
                            profile.ProfileName, profile.LogsDirectoryPath));
                }
                catch (UnauthorizedAccessException)
                {
                    isComplete = false;
                    rejections.Add(new WurmLogLocationRejection(filePath, WurmLogLocationRejectionReason.AccessDenied));
                }
                catch (IOException)
                {
                    isComplete = false;
                    rejections.Add(new WurmLogLocationRejection(filePath, WurmLogLocationRejectionReason.IoFailure));
                }
                catch (ArgumentException)
                {
                    isComplete = false;
                    rejections.Add(new WurmLogLocationRejection(filePath, WurmLogLocationRejectionReason.InvalidStructure));
                }
                catch (NotSupportedException)
                {
                    isComplete = false;
                    rejections.Add(new WurmLogLocationRejection(filePath, WurmLogLocationRejectionReason.InvalidStructure));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            isComplete = false;
            rejections.Add(new WurmLogLocationRejection(profile.LogsDirectoryPath, WurmLogLocationRejectionReason.AccessDenied));
        }
        catch (IOException)
        {
            isComplete = false;
            rejections.Add(new WurmLogLocationRejection(profile.LogsDirectoryPath, WurmLogLocationRejectionReason.IoFailure));
        }
        return files.OrderBy(file => file.FilePath, WurmDiscoveryPathPolicy.Comparer).ToArray();
    }

    private bool TryProbeDirectory(string path, out WurmLogLocationRejectionReason rejectionReason)
    {
        var result = _probeDirectory(path);
        rejectionReason = result switch
        {
            WurmLogDirectoryProbeResult.Exists => default,
            WurmLogDirectoryProbeResult.Missing => WurmLogLocationRejectionReason.Missing,
            WurmLogDirectoryProbeResult.AccessDenied => WurmLogLocationRejectionReason.AccessDenied,
            WurmLogDirectoryProbeResult.IoFailure => WurmLogLocationRejectionReason.IoFailure,
            _ => WurmLogLocationRejectionReason.IoFailure
        };
        return result == WurmLogDirectoryProbeResult.Exists;
    }

    private static WurmLogDirectoryProbeResult ProbeDirectory(string path)
    {
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            _ = enumerator.MoveNext();
            return WurmLogDirectoryProbeResult.Exists;
        }
        catch (FileNotFoundException) { return WurmLogDirectoryProbeResult.Missing; }
        catch (DirectoryNotFoundException) { return WurmLogDirectoryProbeResult.Missing; }
        catch (UnauthorizedAccessException) { return WurmLogDirectoryProbeResult.AccessDenied; }
        catch (IOException) { return WurmLogDirectoryProbeResult.IoFailure; }
        catch (ArgumentException) { return WurmLogDirectoryProbeResult.IoFailure; }
        catch (NotSupportedException) { return WurmLogDirectoryProbeResult.IoFailure; }
    }

    private static readonly Regex SkillsFileNamePattern = new(
        @"^_Skills\.(?<year>\d{4})-(?<month>\d{2})(?:-(?<day>\d{2}))?\.txt\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly record struct ResolvedDirectory(string CanonicalPath, string? PhysicalIdentity, string? LogicalPath = null);
    private sealed record ProfileCandidate(WurmLogProfileLocation Location, ResolvedDirectory LogsDirectory);
    private readonly record struct LocationInput(string Path, string? RuleId, string? Game, bool IsManual)
    {
        public WurmLogLocationOrigin ToOrigin() => new(Path, RuleId, Game, IsManual);
    }
    private sealed record RootCandidate(string PlayersDirectoryPath, IReadOnlyList<ProfileCandidate> Profiles, List<WurmLogLocationOrigin> Origins, bool IsComplete);
}
