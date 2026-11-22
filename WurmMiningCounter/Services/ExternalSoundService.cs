using System.IO;
using System.Media;

namespace WurmMiningCounter.Services;

public sealed class ExternalSoundService
{
    private readonly string _soundsDirectoryPath;
    private readonly Action<string> _play;

    public static string SoundsDirectoryPath => GetSoundsDirectoryPath(AppContext.BaseDirectory);

    public ExternalSoundService()
        : this(AppContext.BaseDirectory, path => new SoundPlayer(path).Play()) { }

    internal ExternalSoundService(string baseDirectory, Action<string> play)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _play = play ?? throw new ArgumentNullException(nameof(play));
        _soundsDirectoryPath = GetSoundsDirectoryPath(baseDirectory);
    }

    internal static string GetSoundsDirectoryPath(string baseDirectory) => Path.Combine(baseDirectory, "Sounds");

    public void Play(string? soundFileName)
    {
        try
        {
            if (!TryGetSoundPath(soundFileName, out var soundPath) || !File.Exists(soundPath))
                return;
            _play(soundPath);
        }
        catch
        {
            // Audio failures must not interrupt the caller.
        }
    }

    internal bool TryGetSoundPath(string? soundFileName, out string soundPath)
    {
        soundPath = string.Empty;
        if (!EventAlertSoundCatalog.IsSafeWavFileName(soundFileName))
            return false;

        var directory = Path.GetFullPath(_soundsDirectoryPath);
        var candidate = Path.GetFullPath(Path.Combine(directory, soundFileName!));
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        soundPath = candidate;
        return true;
    }
}
