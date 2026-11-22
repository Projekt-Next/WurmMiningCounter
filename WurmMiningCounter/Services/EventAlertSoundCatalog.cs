using System.Collections.Immutable;
using System.IO;

namespace WurmMiningCounter.Services;

/// <summary>Reads user-provided WAV choices directly from the application Sounds folder.</summary>
public sealed class EventAlertSoundCatalog
{
    private readonly string _soundsDirectoryPath;

    public EventAlertSoundCatalog() : this(ExternalSoundService.SoundsDirectoryPath) { }

    internal EventAlertSoundCatalog(string soundsDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundsDirectoryPath);
        _soundsDirectoryPath = Path.GetFullPath(soundsDirectoryPath);
    }

    public ImmutableArray<string> GetSnapshot()
    {
        try
        {
            if (!Directory.Exists(_soundsDirectoryPath)) return [];
            return [.. Directory.EnumerateFiles(_soundsDirectoryPath, "*", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileName(path)!)
                .Where(IsSafeWavFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public static bool IsSafeWavFileName(string? soundFileName) =>
        !string.IsNullOrWhiteSpace(soundFileName)
        && !Path.IsPathRooted(soundFileName)
        && string.Equals(Path.GetFileName(soundFileName), soundFileName, StringComparison.Ordinal)
        && string.Equals(Path.GetExtension(soundFileName), ".wav", StringComparison.OrdinalIgnoreCase)
        && !soundFileName.Contains(Path.DirectorySeparatorChar)
        && !soundFileName.Contains(Path.AltDirectorySeparatorChar);
}
