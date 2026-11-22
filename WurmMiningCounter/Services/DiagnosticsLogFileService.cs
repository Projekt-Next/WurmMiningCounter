using System.Text;
using System.IO;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class DiagnosticsLogFileService
{
    private readonly Action<string, string> _writeAllText;

    public DiagnosticsLogFileService(Action<string, string>? writeAllText = null)
    {
        _writeAllText = writeAllText
            ?? new Action<string, string>((path, content) =>
                File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
    }

    public bool TrySave(
        string filePath,
        IReadOnlyList<RuntimeDiagnosticEntry> entries,
        out string? errorMessage)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(entries);
            _writeAllText(filePath, Format(entries));
            errorMessage = null;
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    public static string CreateDefaultFileName(DateTime localNow) =>
        $"WurmMiningCounter-Diagnostics-{localNow:yyyy-MM-dd-HHmm}.txt";

    public static string Format(IReadOnlyList<RuntimeDiagnosticEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var sanitizedEntries = new DiagnosticsExportSanitizer().Sanitize(entries);
        var builder = new StringBuilder();
        foreach (var entry in sanitizedEntries)
            builder.Append(entry.LocalTimestamp.ToString("O"))
                .Append(" | ")
                .Append(entry.Level)
                .Append(" | ")
                .Append(entry.Source)
                .Append(" | ")
                .AppendLine(entry.Message);

        return builder.ToString();
    }
}
