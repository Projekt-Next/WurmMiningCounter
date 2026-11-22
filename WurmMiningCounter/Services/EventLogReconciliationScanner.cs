using System.IO;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class EventLogReconciliationScanner
{
    private readonly Func<string, IEnumerable<string>> _enumerateDirectories;
    private readonly Func<string, IEnumerable<string>> _enumerateFiles;

    public EventLogReconciliationScanner()
        : this(Directory.EnumerateDirectories, path => Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
    {
    }

    internal EventLogReconciliationScanner(
        Func<string, IEnumerable<string>> enumerateDirectories,
        Func<string, IEnumerable<string>> enumerateFiles)
    {
        _enumerateDirectories = enumerateDirectories ?? throw new ArgumentNullException(nameof(enumerateDirectories));
        _enumerateFiles = enumerateFiles ?? throw new ArgumentNullException(nameof(enumerateFiles));
    }

    public IReadOnlyList<EventLogFileCandidate> Scan(string playersDirectoryPath)
    {
        string normalizedPlayersDirectoryPath;
        try
        {
            normalizedPlayersDirectoryPath = NormalizePath(playersDirectoryPath);
        }
        catch (ArgumentException)
        {
            return [];
        }

        if (!Directory.Exists(normalizedPlayersDirectoryPath))
            return [];

        var candidates = new List<EventLogFileCandidate>();

        try
        {
            foreach (var profileDirectoryPath in _enumerateDirectories(normalizedPlayersDirectoryPath))
                ScanProfile(profileDirectoryPath, candidates);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return candidates
            .OrderBy(candidate => candidate.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.EventFilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Catalogues one already-approved profile directory without discovering any roots or siblings.</summary>
    internal IReadOnlyList<EventLogFileCandidate> ScanKnownProfile(
        string profileName,
        string profileDirectoryPath,
        string logsDirectoryPath,
        bool allowLinkedLogs = false)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return [];

        var candidates = new List<EventLogFileCandidate>();
        ScanProfile(profileDirectoryPath, logsDirectoryPath, profileName, candidates, allowLinkedLogs);
        return candidates
            .OrderBy(candidate => candidate.EventFilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ScanProfile(string profileDirectoryPath, ICollection<EventLogFileCandidate> candidates)
        => ScanProfile(profileDirectoryPath, Path.Combine(profileDirectoryPath, "logs"),
            Path.GetFileName(profileDirectoryPath), candidates);

    private void ScanProfile(string profileDirectoryPath, string logsDirectoryPath, string profileName,
        ICollection<EventLogFileCandidate> candidates, bool allowLinkedLogs = false)
    {
        try
        {
            var normalizedProfileDirectoryPath = NormalizePath(profileDirectoryPath);
            var normalizedLogsDirectoryPath = NormalizePath(logsDirectoryPath);
            if ((!allowLinkedLogs && !string.Equals(Path.GetDirectoryName(normalizedLogsDirectoryPath), normalizedProfileDirectoryPath,
                    StringComparison.OrdinalIgnoreCase))
                || !Directory.Exists(normalizedLogsDirectoryPath))
                return;

            foreach (var filePath in _enumerateFiles(normalizedLogsDirectoryPath))
            {
                try
                {
                    var normalizedFilePath = NormalizePath(filePath);
                    if (!EventLogFileNameMatcher.IsMatch(Path.GetFileName(normalizedFilePath)))
                        continue;

                    var fileInfo = new FileInfo(normalizedFilePath);
                    candidates.Add(new EventLogFileCandidate
                    {
                        ProfileName = profileName,
                        ProfileDirectoryPath = normalizedProfileDirectoryPath,
                        EventFilePath = normalizedFilePath,
                        CreationTimeUtc = fileInfo.CreationTimeUtc
                    });
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (ArgumentException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArgumentException)
        {
        }
    }

    private static string NormalizePath(string path)
    {
        var normalizedPath = Path.GetFullPath(path.Trim());
        var rootPath = Path.GetPathRoot(normalizedPath);
        return string.Equals(normalizedPath, rootPath, StringComparison.OrdinalIgnoreCase)
            ? normalizedPath
            : normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
