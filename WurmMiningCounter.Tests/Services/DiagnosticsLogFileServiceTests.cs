using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class DiagnosticsLogFileServiceTests
{
    [Fact]
    public void TrySaveWritesTheCurrentEntriesAsLocalTimeText()
    {
        var writtenPath = string.Empty;
        var writtenContent = string.Empty;
        var service = new DiagnosticsLogFileService((path, content) =>
        {
            writtenPath = path;
            writtenContent = content;
        });
        var entries = new[]
        {
            new RuntimeDiagnosticEntry(
                new DateTimeOffset(2026, 8, 24, 14, 30, 0, TimeSpan.Zero),
                RuntimeDiagnosticLevel.Warning,
                "EventLogReader",
                "Read retry requested")
        };

        var saved = service.TrySave("C:\\temp\\diagnostics.txt", entries, out var errorMessage);

        Assert.True(saved);
        Assert.Null(errorMessage);
        Assert.Equal("C:\\temp\\diagnostics.txt", writtenPath);
        Assert.Equal(
            $"{entries[0].Timestamp.ToLocalTime():O} | Warning | EventLogReader | Read retry requested" + Environment.NewLine,
            writtenContent);
    }

    [Theory]
    [InlineData(2026, 1, 15)]
    [InlineData(2026, 7, 1)]
    public void FormatUsesSystemLocalOffsetIncludingDaylightSavingRules(int year, int month, int day)
    {
        var timestamp = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);
        var entry = new RuntimeDiagnosticEntry(timestamp, RuntimeDiagnosticLevel.Info, "Test", "Message");

        var text = DiagnosticsLogFileService.Format([entry]);
        var expectedLocalTimestamp = timestamp.ToLocalTime();

        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(timestamp), expectedLocalTimestamp.Offset);
        Assert.StartsWith(expectedLocalTimestamp.ToString("O") + " | Info | Test | Message", text);
    }

    [Fact]
    public void TrySaveReturnsReadableFailureInsteadOfThrowing()
    {
        var service = new DiagnosticsLogFileService((_, _) => throw new IOException("The destination is unavailable."));

        var exception = Record.Exception(() =>
        {
            var saved = service.TrySave("C:\\temp\\diagnostics.txt", [], out var errorMessage);
            Assert.False(saved);
            Assert.Equal("The destination is unavailable.", errorMessage);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void DefaultFileNameUsesTheRequiredTimestampFormat()
    {
        var fileName = DiagnosticsLogFileService.CreateDefaultFileName(new DateTime(2026, 8, 24, 14, 5, 0));

        Assert.Equal("WurmMiningCounter-Diagnostics-2026-08-24-1405.txt", fileName);
    }
}
