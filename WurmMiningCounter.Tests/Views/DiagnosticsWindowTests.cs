using WurmMiningCounter.Models;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Tests.Views;

public sealed class DiagnosticsWindowTests
{
    [Fact]
    public void CreateCopyTextFormatsOneSelectedEntry()
    {
        var entry = new RuntimeDiagnosticEntry(
            new DateTimeOffset(2026, 8, 24, 14, 30, 0, TimeSpan.Zero),
            RuntimeDiagnosticLevel.Warning,
            "EventLogReader",
            "Read retry requested");

        var text = DiagnosticsWindow.CreateCopyText([entry]);

        Assert.Equal(
            $"{entry.Timestamp.ToLocalTime():O} | Warning | EventLogReader | Read retry requested" + Environment.NewLine,
            text);
    }

    [Fact]
    public void CreateCopyTextFormatsMultipleSelectedEntriesInProvidedOrder()
    {
        var first = new RuntimeDiagnosticEntry(
            new DateTimeOffset(2026, 8, 24, 14, 30, 0, TimeSpan.Zero),
            RuntimeDiagnosticLevel.Info,
            "App",
            "Application started.");
        var second = new RuntimeDiagnosticEntry(
            new DateTimeOffset(2026, 8, 24, 14, 31, 0, TimeSpan.Zero),
            RuntimeDiagnosticLevel.Error,
            "SettingsService",
            "Settings save failed.");

        var text = DiagnosticsWindow.CreateCopyText([first, second]);

        Assert.Equal(
            $"{first.Timestamp.ToLocalTime():O} | Info | App | Application started." + Environment.NewLine
            + $"{second.Timestamp.ToLocalTime():O} | Error | SettingsService | Settings save failed." + Environment.NewLine,
            text);
    }
}
