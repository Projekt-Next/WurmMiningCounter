namespace WurmMiningCounter.Models;

public sealed class KnownLogLocation
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Game { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Path { get; set; }
    public int? AppId { get; set; }
    public string? PlayersRelativePath { get; set; }
}

public static class KnownLogLocations
{
    public const string DirectType = "Direct";
    public const string SteamType = "Steam";

    public static List<KnownLogLocation> CreateDefaults() =>
    [
        new()
        {
            Id = "wurm-online-standard",
            Enabled = true,
            Game = "WurmOnline",
            Type = DirectType,
            Path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "wurm", "players")
        },
        new()
        {
            Id = "wurm-online-steam",
            Enabled = true,
            Game = "WurmOnline",
            Type = SteamType,
            AppId = 1179680,
            PlayersRelativePath = System.IO.Path.Combine("gamedata", "players")
        },
        new()
        {
            Id = "wurm-unlimited-steam",
            Enabled = true,
            Game = "WurmUnlimited",
            Type = SteamType,
            AppId = 366220,
            PlayersRelativePath = System.IO.Path.Combine("WurmLauncher", "PlayerFiles", "players")
        }
    ];

    public static bool TryValidate(KnownLogLocation? location, out string? error)
    {
        error = null;
        if (location is null || string.IsNullOrWhiteSpace(location.Id)
            || string.IsNullOrWhiteSpace(location.Game) || string.IsNullOrWhiteSpace(location.Type))
        {
            error = "Id, Game and Type are required.";
            return false;
        }

        if (string.Equals(location.Type, DirectType, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(location.Path))
                return true;
            error = "A Direct location requires Path.";
            return false;
        }

        if (string.Equals(location.Type, SteamType, StringComparison.OrdinalIgnoreCase))
        {
            if (location.AppId > 0 && !string.IsNullOrWhiteSpace(location.PlayersRelativePath)
                && IsSafeRelativePath(location.PlayersRelativePath))
                return true;
            error = "A Steam location requires a positive AppId and a safe relative PlayersRelativePath.";
            return false;
        }

        error = "Type must be Direct or Steam.";
        return false;
    }

    public static KnownLogLocation Copy(KnownLogLocation location) => new()
    {
        Id = location.Id,
        Enabled = location.Enabled,
        Game = location.Game,
        Type = location.Type,
        Path = location.Path,
        AppId = location.AppId,
        PlayersRelativePath = location.PlayersRelativePath
    };

    public static List<KnownLogLocation> CopyAll(IEnumerable<KnownLogLocation?> locations) => locations
        .Where(location => location is not null)
        .Select(location => Copy(location!))
        .ToList();

    public static bool TryResolveRelativePath(string rootPath, string relativePath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootPath) || !IsSafeRelativePath(relativePath))
            return false;

        try
        {
            var normalizedRoot = System.IO.Path.GetFullPath(rootPath);
            var normalizedCandidate = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(normalizedRoot, relativePath));
            var rootWithSeparator = normalizedRoot.EndsWith(System.IO.Path.DirectorySeparatorChar)
                || normalizedRoot.EndsWith(System.IO.Path.AltDirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + System.IO.Path.DirectorySeparatorChar;
            if (!normalizedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return false;

            resolvedPath = normalizedCandidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (System.IO.IOException)
        {
            return false;
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (System.IO.Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal))
            return false;

        return path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .All(segment => !string.Equals(segment, ".", StringComparison.Ordinal)
                && !string.Equals(segment, "..", StringComparison.Ordinal));
    }
}
