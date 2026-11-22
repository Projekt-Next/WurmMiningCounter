using System.IO;

namespace WurmMiningCounter.Services;

internal static class WurmDiscoveryPathPolicy
{
    internal static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    internal static bool NameIs(string path, string name) =>
        string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        var rootPath = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static string CanonicalizeDirectory(string path)
    {
        var fullPath = NormalizeDirectoryPath(path);
        var directory = new DirectoryInfo(fullPath);
        var target = directory.Exists
            ? directory.ResolveLinkTarget(returnFinalTarget: true)
            : null;
        return NormalizeDirectoryPath(target?.FullName ?? directory.FullName);
    }
}
