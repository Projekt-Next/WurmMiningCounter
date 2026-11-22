using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public enum SteamDiscoveryIssue { SteamNotFound, LibraryUnavailable, AccessDenied, IoFailure, MetadataInvalid }
public sealed record SteamDiscoveryDiagnostic(string Path, SteamDiscoveryIssue Issue);
public sealed record SteamInstallationDiscoveryResult(IReadOnlyDictionary<int, IReadOnlyList<string>> InstallationsByAppId, IReadOnlyList<SteamDiscoveryDiagnostic> Diagnostics);
internal sealed record SteamRootDiscovery(IReadOnlyList<string> Roots, IReadOnlyList<SteamDiscoveryDiagnostic> Diagnostics);
internal enum SteamPathProbeResult { Exists, Missing, AccessDenied, IoFailure }

public sealed class SteamInstallationLocator
{
    private const long MaximumMetadataBytes = 1024 * 1024;
    private const int MaximumVdfNestingDepth = 64;
    private readonly Func<SteamRootDiscovery> _roots;
    private readonly Func<string, SteamPathProbeResult> _fileProbe, _directoryProbe;
    private readonly Func<string, long> _length;
    private readonly Func<string, string> _read;
    private readonly Func<string, string?> _identity;

    public SteamInstallationLocator() : this(GetDefaultSteamRoots, ProbeFile, ProbeDirectory, path => new FileInfo(path).Length, File.ReadAllText, new WindowsDirectoryPhysicalIdentityProvider().GetIdentity) { }
    internal SteamInstallationLocator(Func<IEnumerable<string>> roots, Func<string, bool> fileExists, Func<string, string> read)
        : this(() => new SteamRootDiscovery(roots().ToArray(), []), path => fileExists(path) ? SteamPathProbeResult.Exists : SteamPathProbeResult.Missing, _ => SteamPathProbeResult.Exists, path => read(path).Length, read, _ => null) { }
    internal SteamInstallationLocator(Func<IEnumerable<string>> roots, Func<string, bool> fileExists, Func<string, bool> directoryExists, Func<string, long> length, Func<string, string> read)
        : this(() => new SteamRootDiscovery(roots().ToArray(), []), path => fileExists(path) ? SteamPathProbeResult.Exists : SteamPathProbeResult.Missing, path => directoryExists(path) ? SteamPathProbeResult.Exists : SteamPathProbeResult.Missing, length, read, _ => null) { }
    internal SteamInstallationLocator(Func<SteamRootDiscovery> roots, Func<string, SteamPathProbeResult> fileProbe, Func<string, SteamPathProbeResult> directoryProbe, Func<string, long> length, Func<string, string> read, Func<string, string?> identity)
    { _roots = roots; _fileProbe = fileProbe; _directoryProbe = directoryProbe; _length = length; _read = read; _identity = identity; }

    public IReadOnlyList<string> Locate(int appId) => LocateMany([appId]).InstallationsByAppId.GetValueOrDefault(appId, []);
    public SteamInstallationDiscoveryResult LocateMany(IEnumerable<int> appIds)
    {
        ArgumentNullException.ThrowIfNull(appIds);
        var requested = appIds.Where(id => id > 0).Distinct().Order().ToArray();
        var found = requested.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var diagnostics = new List<SteamDiscoveryDiagnostic>();
        if (requested.Length == 0) return Result(found, diagnostics);
        var roots = ExistingRoots(diagnostics);
        if (roots.Count == 0) return Result(found, diagnostics);
        var identities = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var libraryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in Libraries(roots, diagnostics))
        {
            if (!libraryKeys.Add(DirectoryKey(library, identities)) || !Available(library, diagnostics)) continue;
            foreach (var appId in requested) AddInstallation(library, appId, found[appId], diagnostics);
        }
        return Result(found, diagnostics);
    }

    private IReadOnlyList<string> ExistingRoots(ICollection<SteamDiscoveryDiagnostic> diagnostics)
    {
        var discovery = _roots(); foreach (var item in discovery.Diagnostics) diagnostics.Add(item);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in discovery.Roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try { var path = Normalize(raw); if (Exists(path, _directoryProbe(path), diagnostics, SteamDiscoveryIssue.LibraryUnavailable)) roots.Add(path); }
            catch (UnauthorizedAccessException ex) { diagnostics.Add(new(ex.Message, SteamDiscoveryIssue.AccessDenied)); }
            catch (IOException ex) { diagnostics.Add(new(ex.Message, SteamDiscoveryIssue.IoFailure)); }
            catch (ArgumentException ex) { diagnostics.Add(new(ex.Message, SteamDiscoveryIssue.MetadataInvalid)); }
        }
        if (roots.Count == 0 && !diagnostics.Any(x => x.Issue is SteamDiscoveryIssue.AccessDenied or SteamDiscoveryIssue.IoFailure)) diagnostics.Add(new("Steam", SteamDiscoveryIssue.SteamNotFound));
        return roots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IReadOnlyList<string> Libraries(IReadOnlyList<string> roots, ICollection<SteamDiscoveryDiagnostic> diagnostics)
    {
        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var sourcePaths = new[]
            {
                Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                Path.Combine(root, "config", "libraryfolders.vdf")
            };
            foreach (var sourcePath in sourcePaths)
            {
                if (ReadKeyValues(sourcePath, diagnostics, out var document) != ReadStatus.Success)
                    continue;
                if (!Object(document, "libraryfolders", out var folders))
                {
                    diagnostics.Add(new(sourcePath, SteamDiscoveryIssue.MetadataInvalid));
                    continue;
                }
                foreach (var pair in folders.Values)
                {
                    if (!int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                    var path = pair.Value.Text;
                    if (pair.Value.Object is not null && !Text(pair.Value.Object, "path", out path)) { diagnostics.Add(new(sourcePath, SteamDiscoveryIssue.MetadataInvalid)); continue; }
                    if (string.IsNullOrWhiteSpace(path)) { diagnostics.Add(new(sourcePath, SteamDiscoveryIssue.MetadataInvalid)); continue; }
                    try { libraries.Add(Normalize(path)); } catch (ArgumentException) { diagnostics.Add(new(sourcePath, SteamDiscoveryIssue.MetadataInvalid)); }
                }
            }
        }
        return libraries.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void AddInstallation(string library, int appId, ISet<string> found, ICollection<SteamDiscoveryDiagnostic> diagnostics)
    {
        var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
        try
        {
            if (ReadKeyValues(manifest, diagnostics, out var document) != ReadStatus.Success) return;
            if (!Object(document, "AppState", out var state) || !Text(state, "appid", out var idText) || !int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var manifestId) || manifestId != appId || !Text(state, "installdir", out var installDir)) { diagnostics.Add(new(manifest, SteamDiscoveryIssue.MetadataInvalid)); return; }
            if (!KnownLogLocations.TryResolveRelativePath(Path.Combine(library, "steamapps", "common"), installDir, out var installation)) { diagnostics.Add(new(manifest, SteamDiscoveryIssue.MetadataInvalid)); return; }
            found.Add(installation);
        }
        catch (UnauthorizedAccessException ex) { diagnostics.Add(new(ex.Message, SteamDiscoveryIssue.AccessDenied)); }
        catch (IOException ex) { diagnostics.Add(new(ex.Message, SteamDiscoveryIssue.IoFailure)); }
        catch (ArgumentException ex) { diagnostics.Add(new(ex.Message, SteamDiscoveryIssue.MetadataInvalid)); }
    }

    private bool Available(string path, ICollection<SteamDiscoveryDiagnostic> diagnostics) => Exists(path, _directoryProbe(path), diagnostics, SteamDiscoveryIssue.LibraryUnavailable);
    private ReadStatus ReadKeyValues(string path, ICollection<SteamDiscoveryDiagnostic> diagnostics, out KeyValuesObject document)
    {
        document = null!;
        try
        {
            var probe = _fileProbe(path);
            if (!Exists(path, probe, diagnostics, SteamDiscoveryIssue.MetadataInvalid)) return probe == SteamPathProbeResult.Missing ? ReadStatus.Missing : ReadStatus.Failed;
            if (_length(path) > MaximumMetadataBytes || !ValveKeyValues.TryParse(_read(path), MaximumVdfNestingDepth, out document)) { diagnostics.Add(new(path, SteamDiscoveryIssue.MetadataInvalid)); return ReadStatus.Failed; }
            return ReadStatus.Success;
        }
        catch (UnauthorizedAccessException) { diagnostics.Add(new(path, SteamDiscoveryIssue.AccessDenied)); return ReadStatus.Failed; }
        catch (IOException) { diagnostics.Add(new(path, SteamDiscoveryIssue.IoFailure)); return ReadStatus.Failed; }
    }

    private static bool Exists(string path, SteamPathProbeResult probe, ICollection<SteamDiscoveryDiagnostic> diagnostics, SteamDiscoveryIssue missing)
    { if (probe == SteamPathProbeResult.Exists) return true; if (probe == SteamPathProbeResult.AccessDenied) diagnostics.Add(new(path, SteamDiscoveryIssue.AccessDenied)); else if (probe == SteamPathProbeResult.IoFailure) diagnostics.Add(new(path, SteamDiscoveryIssue.IoFailure)); else if (missing != SteamDiscoveryIssue.MetadataInvalid) diagnostics.Add(new(path, missing)); return false; }
    private string DirectoryKey(string path, IDictionary<string, string?> cache)
    { if (!cache.TryGetValue(path, out var id)) { try { id = _identity(path); } catch (UnauthorizedAccessException) { } catch (IOException) { } cache.Add(path, id); } return id is { Length: > 0 } ? $"id:{id}" : $"path:{path}"; }
    private static SteamInstallationDiscoveryResult Result(IReadOnlyDictionary<int, HashSet<string>> found, IReadOnlyList<SteamDiscoveryDiagnostic> diagnostics) => new(found.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.OrderBy(y => y, StringComparer.OrdinalIgnoreCase).ToArray()), diagnostics.ToArray());
    private static bool Object(KeyValuesObject obj, string key, out KeyValuesObject value) { value = null!; return obj.Values.TryGetValue(key, out var item) && item.Object is not null && (value = item.Object) is not null; }
    private static bool Text(KeyValuesObject obj, string key, out string value) { value = string.Empty; return obj.Values.TryGetValue(key, out var item) && item.Text is not null && (value = item.Text) is not null; }
    private static string Normalize(string path) => Path.GetFullPath(path.Trim());

    private static SteamRootDiscovery GetDefaultSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var diagnostics = new List<SteamDiscoveryDiagnostic>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine }) foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default })
        { try { using var key = RegistryKey.OpenBaseKey(hive, view); using var steam = key.OpenSubKey(@"SOFTWARE\Valve\Steam"); foreach (var name in new[] { "SteamPath", "InstallPath" }) if (steam?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value)) roots.Add(value); }
          catch (UnauthorizedAccessException) { diagnostics.Add(new($"{hive}/{view}:SOFTWARE\\Valve\\Steam", SteamDiscoveryIssue.AccessDenied)); }
          catch (IOException) { diagnostics.Add(new($"{hive}/{view}:SOFTWARE\\Valve\\Steam", SteamDiscoveryIssue.IoFailure)); } }
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); if (!string.IsNullOrWhiteSpace(x86)) roots.Add(Path.Combine(x86, "Steam"));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); if (!string.IsNullOrWhiteSpace(programFiles)) roots.Add(Path.Combine(programFiles, "Steam"));
        return new SteamRootDiscovery(roots.ToArray(), diagnostics);
    }
    private static SteamPathProbeResult ProbeFile(string path) { try { _ = File.GetAttributes(path); return SteamPathProbeResult.Exists; } catch (FileNotFoundException) { return SteamPathProbeResult.Missing; } catch (DirectoryNotFoundException) { return SteamPathProbeResult.Missing; } catch (UnauthorizedAccessException) { return SteamPathProbeResult.AccessDenied; } catch (IOException) { return SteamPathProbeResult.IoFailure; } }
    private static SteamPathProbeResult ProbeDirectory(string path) { try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0 ? SteamPathProbeResult.Exists : SteamPathProbeResult.Missing; } catch (FileNotFoundException) { return SteamPathProbeResult.Missing; } catch (DirectoryNotFoundException) { return SteamPathProbeResult.Missing; } catch (UnauthorizedAccessException) { return SteamPathProbeResult.AccessDenied; } catch (IOException) { return SteamPathProbeResult.IoFailure; } }

    private enum ReadStatus { Success, Missing, Failed }
    private sealed class KeyValuesObject { public Dictionary<string, KeyValuesValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase); }
    private sealed record KeyValuesValue(string? Text, KeyValuesObject? Object);
    private static class ValveKeyValues
    {
        public static bool TryParse(string text, int maximumDepth, out KeyValuesObject document)
        { document = new(); try { var parser = new Parser(text, maximumDepth); parser.ReadObject(document, true, 0); if (!parser.AtEnd) throw new FormatException(); return true; } catch (FormatException) { document = null!; return false; } }
        private sealed class Parser(string text, int maximumDepth)
        {
            private int _index; public bool AtEnd { get { Whitespace(); return _index == text.Length; } }
            public void ReadObject(KeyValuesObject destination, bool root, int depth)
            { if (depth > maximumDepth) throw new FormatException(); if (!root) Expect('{'); while (true) { Whitespace(); if (!root && Consume('}')) return; if (root && _index == text.Length) return; var key = Quoted(); Whitespace(); KeyValuesValue value; if (Peek() == '{') { var child = new KeyValuesObject(); ReadObject(child, false, depth + 1); value = new(null, child); } else value = new(Quoted(), null); if (!destination.Values.TryAdd(key, value)) throw new FormatException(); } }
            private string Quoted() { Expect('"'); var value = new StringBuilder(); while (_index < text.Length) { var c = text[_index++]; if (c == '"') return value.ToString(); if (c != '\\') { value.Append(c); continue; } if (_index == text.Length) throw new FormatException(); value.Append(text[_index++] switch { '\\' => '\\', '"' => '"', 'n' => '\n', 'r' => '\r', 't' => '\t', var escaped => escaped }); } throw new FormatException(); }
            private void Expect(char c) { Whitespace(); if (_index == text.Length || text[_index++] != c) throw new FormatException(); }
            private char Peek() { Whitespace(); return _index < text.Length ? text[_index] : '\0'; }
            private bool Consume(char c) { if (_index < text.Length && text[_index] == c) { _index++; return true; } return false; }
            private void Whitespace() { while (_index < text.Length) { if (char.IsWhiteSpace(text[_index])) { _index++; continue; } if (_index + 1 < text.Length && text[_index] == '/' && text[_index + 1] == '/') { _index += 2; while (_index < text.Length && text[_index] is not '\r' and not '\n') _index++; continue; } return; } }
        }
    }
}
