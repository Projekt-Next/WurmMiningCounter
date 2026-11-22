using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class DiagnosticsViewModelTests
{
    [Fact]
    public void RefreshSnapshotPublishesTheServiceEntries()
    {
        var diagnostics = new RuntimeDiagnosticService();
        diagnostics.Report(RuntimeDiagnosticLevel.Warning, "EventLogReader", "Read retry requested");
        var viewModel = new DiagnosticsViewModel(diagnostics);

        viewModel.RefreshSnapshot();

        var entry = Assert.Single(viewModel.Entries);
        Assert.Equal(RuntimeDiagnosticLevel.Warning, entry.Level);
        Assert.Equal("EventLogReader", entry.Source);
        Assert.Equal("Read retry requested", entry.Message);
    }

    [Fact]
    public void RefreshSnapshotSortsEntriesChronologically()
    {
        var timestamps = new Queue<DateTimeOffset>(
        [
            new DateTimeOffset(2026, 8, 15, 12, 2, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 15, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 15, 12, 3, 0, TimeSpan.Zero)
        ]);
        var diagnostics = new RuntimeDiagnosticService(utcNow: () => timestamps.Dequeue());
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Second");
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "First");
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Third");
        var viewModel = new DiagnosticsViewModel(diagnostics);

        viewModel.RefreshSnapshot();

        Assert.Equal(["First", "Second", "Third"], viewModel.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void RefreshSnapshotKeepsStableOrderForEqualTimestamps()
    {
        var timestamp = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var diagnostics = new RuntimeDiagnosticService(utcNow: () => timestamp);
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "First");
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Second");
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Third");
        var viewModel = new DiagnosticsViewModel(diagnostics);

        viewModel.RefreshSnapshot();
        viewModel.RefreshSnapshot();

        Assert.Equal(["First", "Second", "Third"], viewModel.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void RefreshSnapshotWithNoChangesDoesNotModifyEntries()
    {
        var diagnostics = new RuntimeDiagnosticService();
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Entry");
        var viewModel = new DiagnosticsViewModel(diagnostics);
        viewModel.RefreshSnapshot();
        var original = Assert.Single(viewModel.Entries);
        var changes = 0;
        viewModel.Entries.CollectionChanged += (_, _) => changes++;

        viewModel.RefreshSnapshot();

        Assert.Equal(0, changes);
        Assert.Same(original, Assert.Single(viewModel.Entries));
    }

    [Fact]
    public void RefreshSnapshotAppendsNewEntryWithoutReplacingExistingItems()
    {
        var diagnostics = new RuntimeDiagnosticService();
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Existing");
        var viewModel = new DiagnosticsViewModel(diagnostics);
        viewModel.RefreshSnapshot();
        var existing = Assert.Single(viewModel.Entries);

        diagnostics.Report(RuntimeDiagnosticLevel.Warning, "Test", "New");
        viewModel.RefreshSnapshot();

        Assert.Equal(2, viewModel.Entries.Count);
        Assert.Same(existing, viewModel.Entries[0]);
        Assert.Equal("New", viewModel.Entries[1].Message);
    }

    [Fact]
    public void RefreshSnapshotRemovesEntriesEvictedByServiceLimit()
    {
        var diagnostics = new RuntimeDiagnosticService();
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Oldest");
        var viewModel = new DiagnosticsViewModel(diagnostics);
        viewModel.RefreshSnapshot();
        var oldest = Assert.Single(viewModel.Entries);

        for (var index = 0; index < RuntimeDiagnosticService.MaximumEntries; index++)
            diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", $"Entry {index}");

        viewModel.RefreshSnapshot();

        Assert.Equal(RuntimeDiagnosticService.MaximumEntries, viewModel.Entries.Count);
        Assert.DoesNotContain(viewModel.Entries, entry => ReferenceEquals(entry, oldest));
        Assert.Equal("Entry 0", viewModel.Entries[0].Message);
    }

    [Fact]
    public void ClearSynchronizesEmptySnapshot()
    {
        var diagnostics = new RuntimeDiagnosticService();
        diagnostics.Report(RuntimeDiagnosticLevel.Info, "Test", "Entry");
        var viewModel = new DiagnosticsViewModel(diagnostics);
        viewModel.RefreshSnapshot();

        viewModel.Clear();

        Assert.Empty(viewModel.Entries);
        Assert.Empty(viewModel.GetSnapshot());
    }
}
