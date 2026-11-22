using System.Text.RegularExpressions;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

/// <summary>
/// Removes directly identifying local data from a single Copy or Save Log export.
/// </summary>
internal sealed partial class DiagnosticsExportSanitizer
{
    private readonly Dictionary<string, int> _profileAliases = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RuntimeDiagnosticEntry> Sanitize(IReadOnlyList<RuntimeDiagnosticEntry>? entries)
    {
        try
        {
            if (entries is null)
                return [];

            var sanitizedEntries = new RuntimeDiagnosticEntry[entries.Count];
            for (var index = 0; index < entries.Count; index++)
                sanitizedEntries[index] = SanitizeEntry(entries[index]);

            return sanitizedEntries;
        }
        catch
        {
            // Export must not expose raw diagnostic data if sanitization fails.
            return [];
        }
    }

    private RuntimeDiagnosticEntry SanitizeEntry(RuntimeDiagnosticEntry entry)
    {
        try
        {
            return entry with
            {
                Source = SanitizeText(entry.Source),
                Message = SanitizeMessage(entry.Message)
            };
        }
        catch
        {
            return entry with
            {
                Source = "[REDACTED_SOURCE]",
                Message = "[REDACTED_DIAGNOSTIC]"
            };
        }
    }

    private string SanitizeMessage(string? value)
    {
        var sanitized = SanitizeText(value);
        return ProfileDiagnosticPrefix().Replace(sanitized, match =>
        {
            var profileName = match.Groups["profile"].Value.Trim();
            var eventName = match.Groups["event"].Value;
            return $"{GetProfileAlias(profileName)} | {eventName}";
        });
    }

    private string SanitizeText(string? value)
    {
        var sanitized = value ?? string.Empty;

        try
        {
            var userName = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(userName))
                sanitized = sanitized.Replace(userName, "[REDACTED_USER]", StringComparison.OrdinalIgnoreCase);

            sanitized = WindowsPath().Replace(sanitized, "[REDACTED_PATH]");
            return UnixHomePath().Replace(sanitized, "[REDACTED_PATH]");
        }
        catch
        {
            return "[REDACTED_DIAGNOSTIC]";
        }
    }

    private string GetProfileAlias(string profileName)
    {
        try
        {
            if (!_profileAliases.TryGetValue(profileName, out var aliasNumber))
            {
                aliasNumber = _profileAliases.Count + 1;
                _profileAliases.Add(profileName, aliasNumber);
            }

            return $"Profile-{aliasNumber}";
        }
        catch
        {
            return "Profile-Unknown";
        }
    }

    [GeneratedRegex("(?i)(?:[a-z]:\\\\|\\\\\\\\)[^\\r\\n\"']+?(?=(?: \\| | (?:Generation|Error|ReconciliationRequired)=|$))")]
    private static partial Regex WindowsPath();

    [GeneratedRegex("(?i)(?:/Users/|/home/)[^\\r\\n\"']+?(?=(?: \\| | (?:Generation|Error|ReconciliationRequired)=|$))")]
    private static partial Regex UnixHomePath();

    [GeneratedRegex(@"^(?<profile>[^|\r\n]{1,128}) \| (?<event>Prospecting|VeinContextUpdate|VeinContext|NearbyScan|EventLog|Source discovered|Source baselined|TerminalSourceFault|TerminalMiningFault|TerminalContextFault|ProfileRelease)\b")]
    private static partial Regex ProfileDiagnosticPrefix();
}
