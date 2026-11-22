using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Tests.Services;

public sealed class RuntimeDiagnosticServiceTests
{
    [Fact]
    public void ReportKeepsTheNewestThousandEntriesInOrder()
    {
        var diagnostics = new RuntimeDiagnosticService();

        for (var index = 0; index < RuntimeDiagnosticService.MaximumEntries + 1; index++)
            diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", index.ToString());

        var entries = diagnostics.GetSnapshot();

        Assert.Equal(RuntimeDiagnosticService.MaximumEntries, entries.Count);
        Assert.Equal("1", entries[0].Message);
        Assert.Equal(RuntimeDiagnosticService.MaximumEntries.ToString(), entries[^1].Message);
    }

    [Fact]
    public void ClearRemovesAllEntries()
    {
        var diagnostics = new RuntimeDiagnosticService();
        diagnostics.Report(RuntimeDiagnosticLevel.Error, "Test", "Failure");

        diagnostics.Clear();

        Assert.Empty(diagnostics.GetSnapshot());
    }

    [Fact]
    public void DisabledDiagnosticsDoNotStoreEntries()
    {
        var diagnostics = new RuntimeDiagnosticService(enabled: false);

        diagnostics.Report(RuntimeDiagnosticLevel.Warning, "Test", "Ignored");

        Assert.Empty(diagnostics.GetSnapshot());
    }

    [Fact]
    public void ConcurrentReportingIsSafeAndBounded()
    {
        var diagnostics = new RuntimeDiagnosticService();

        Parallel.For(0, 10_000, index =>
            diagnostics.Report(RuntimeDiagnosticLevel.Info, "Concurrent", index.ToString()));

        Assert.Equal(RuntimeDiagnosticService.MaximumEntries, diagnostics.GetSnapshot().Count);
    }

    [Fact]
    public void ReportingNeverThrowsWhenFormattingOrClockFails()
    {
        var diagnostics = new RuntimeDiagnosticService(utcNow: () => throw new InvalidOperationException("Clock failure"));

        var exception = Record.Exception(() => diagnostics.Report(
            RuntimeDiagnosticLevel.Error,
            "Test",
            "{0}",
            new ThrowingFormatValue()));

        Assert.Null(exception);
        Assert.Empty(diagnostics.GetSnapshot());
    }

    [Fact]
    public void ReportLimitsMessageToEightKiB()
    {
        var diagnostics = new RuntimeDiagnosticService();
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", new string('ą', 10_000));

        var entry = Assert.Single(diagnostics.GetSnapshot());

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(entry.Message) <= RuntimeDiagnosticService.MaximumMessageBytes);
    }

    [Fact]
    public void SnapshotRetainsOriginalProfileAndPath()
    {
        var diagnostics = new RuntimeDiagnosticService();
        var localPath = @"C:\Users\local-user\wurm\players\profilea\logs\_Event.2026-08.txt";

        diagnostics.ReportProfile(RuntimeDiagnosticLevel.Info, "Test", "profilea", "Prospecting", "Start");
        diagnostics.Report(RuntimeDiagnosticLevel.Warning, "Test", "Operation failed for {0}", localPath);

        var messages = diagnostics.GetSnapshot().Select(entry => entry.Message).ToArray();

        Assert.Equal("profilea | Prospecting | Start", messages[0]);
        Assert.Contains(localPath, messages[1]);
    }

    [Fact]
    public void ViewModelReceivesFullDiagnosticData()
    {
        var diagnostics = new RuntimeDiagnosticService();
        var localPath = @"C:\Users\local-user\wurm\players\profilea\logs\_Event.2026-08.txt";

        diagnostics.ReportProfile(RuntimeDiagnosticLevel.Info, "Test", "profilea", "EventLog", "Segment=" + localPath);
        var viewModel = new DiagnosticsViewModel(diagnostics);
        viewModel.RefreshSnapshot();

        var entry = Assert.Single(viewModel.Entries);
        Assert.Equal("profilea | EventLog | Segment=" + localPath, entry.Message);
    }

    [Fact]
    public void UiPresentationUsesLocalTimeWithoutChangingStoredUtcTimestamp()
    {
        var utcTimestamp = new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero);
        var diagnostics = new RuntimeDiagnosticService(utcNow: () => utcTimestamp);
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Message");
        var viewModel = new DiagnosticsViewModel(diagnostics);

        viewModel.RefreshSnapshot();

        var entry = Assert.Single(viewModel.Entries);
        Assert.Equal(utcTimestamp, entry.Timestamp);
        Assert.Equal(TimeSpan.Zero, entry.Timestamp.Offset);
        Assert.Equal(utcTimestamp.ToLocalTime(), entry.LocalTimestamp);
    }

    [Fact]
    public void ReportNormalizesInjectedClockTimestampToUtc()
    {
        var clockTimestamp = new DateTimeOffset(2026, 1, 15, 13, 30, 0, TimeSpan.FromHours(1));
        var diagnostics = new RuntimeDiagnosticService(utcNow: () => clockTimestamp);

        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Message");

        var entry = Assert.Single(diagnostics.GetSnapshot());
        Assert.Equal(clockTimestamp.ToUniversalTime(), entry.Timestamp);
        Assert.Equal(TimeSpan.Zero, entry.Timestamp.Offset);
    }

    [Fact]
    public void CopySelectedAndCopyAllSanitizeExportedDataWithStableAliases()
    {
        var diagnostics = new RuntimeDiagnosticService();
        var userName = Environment.UserName;
        var localPath = $@"C:\Users\{userName}\wurm\players\profilea\logs\_Event.2026-08.txt";
        diagnostics.ReportProfile(RuntimeDiagnosticLevel.Info, "Test", "profilea", "Prospecting", "Start");
        diagnostics.ReportProfile(RuntimeDiagnosticLevel.Info, "Test", "profilea", "Prospecting", "Stop");
        diagnostics.ReportProfile(RuntimeDiagnosticLevel.Info, "Test", "profileb", "EventLog", "Segment=_Event.2026-08.txt");
        diagnostics.Report(RuntimeDiagnosticLevel.Warning, "Test", "Read failure at " + localPath);
        var viewModel = new DiagnosticsViewModel(diagnostics);
        var copyAllText = viewModel.CreateCopyText();
        var selectedText = DiagnosticsWindow.CreateCopyText(viewModel.GetSnapshot().Take(2).ToArray());

        Assert.DoesNotContain("profilea", copyAllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profileb", copyAllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users\\", copyAllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userName, copyAllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Profile-1 | Prospecting | Start", copyAllText);
        Assert.Contains("Profile-1 | Prospecting | Stop", copyAllText);
        Assert.Contains("Profile-2 | EventLog | Segment=_Event.2026-08.txt", copyAllText);
        Assert.DoesNotContain("profilea", selectedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Profile-1 | Prospecting | Start", selectedText);
        Assert.Contains("Profile-1 | Prospecting | Stop", selectedText);
    }

    [Fact]
    public void SaveLogSanitizesExportedData()
    {
        var diagnostics = new RuntimeDiagnosticService();
        var userName = Environment.UserName;
        var localPath = $@"C:\Users\{userName}\wurm\players\profilea\logs\_Event.2026-08.txt";
        diagnostics.ReportProfile(RuntimeDiagnosticLevel.Error, "Test", "profilea", "EventLog", "Failure=" + new IOException(localPath));
        var savedText = string.Empty;
        var logFileService = new DiagnosticsLogFileService((_, content) => savedText = content);

        Assert.True(logFileService.TrySave("diagnostics.txt", diagnostics.GetSnapshot(), out _));
        Assert.DoesNotContain("profilea", savedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users\\", savedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userName, savedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Profile-1 | EventLog", savedText);
        Assert.Contains("[REDACTED_PATH]", savedText);
    }

    [Theory]
    [InlineData("Source discovered")]
    [InlineData("Source baselined")]
    [InlineData("TerminalSourceFault")]
    [InlineData("TerminalMiningFault")]
    [InlineData("TerminalContextFault")]
    [InlineData("ProfileRelease")]
    public void EventPipelineProfileDiagnosticsAreSanitizedForCopyAndSave(string eventName)
    {
        const string profileName = "actual-profile";
        var userName = Environment.UserName;
        var localPath = $@"C:\Users\{userName}\wurm\players\{profileName}\logs\_Event.2026-08.txt";
        var entries = new[]
        {
            new RuntimeDiagnosticEntry(
                new DateTimeOffset(2026, 8, 24, 14, 30, 0, TimeSpan.Zero),
                RuntimeDiagnosticLevel.Error,
                "EventPipeline",
                $"{profileName} | {eventName} | Segment={localPath} Generation=17 Error=IOException")
        };
        var savedText = string.Empty;
        var logFileService = new DiagnosticsLogFileService((_, content) => savedText = content);

        var copiedText = DiagnosticsWindow.CreateCopyText(entries);
        Assert.True(logFileService.TrySave("diagnostics.txt", entries, out _));

        foreach (var text in new[] { copiedText, savedText })
        {
            Assert.DoesNotContain(profileName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\Users\\", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(userName, text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"Profile-1 | {eventName} | Segment=[REDACTED_PATH] Generation=17 Error=IOException", text);
        }
    }

    [Fact]
    public void EventPipelineProfileDiagnosticsUseOneStableAliasPerExport()
    {
        const string profileName = "actual-profile";
        var entries = new[]
        {
            new RuntimeDiagnosticEntry(
                new DateTimeOffset(2026, 8, 24, 14, 30, 0, TimeSpan.Zero),
                RuntimeDiagnosticLevel.Info,
                "EventPipeline",
                $"{profileName} | Source discovered | Segment=_Event.2026-08.txt Generation=17"),
            new RuntimeDiagnosticEntry(
                new DateTimeOffset(2026, 8, 24, 14, 31, 0, TimeSpan.Zero),
                RuntimeDiagnosticLevel.Error,
                "FastEventDispatcher",
                $"{profileName} | TerminalContextFault | Segment=_Event.2026-08.txt Generation=17 Error=IOException")
        };

        var text = DiagnosticsWindow.CreateCopyText(entries);

        Assert.Contains("Profile-1 | Source discovered", text);
        Assert.Contains("Profile-1 | TerminalContextFault", text);
        Assert.DoesNotContain("Profile-2", text);
    }

    private sealed class ThrowingFormatValue
    {
        public override string ToString() => throw new InvalidOperationException("Formatting failure");
    }
}
