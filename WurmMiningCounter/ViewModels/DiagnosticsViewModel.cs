using System.Collections.ObjectModel;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.ViewModels;

public sealed class DiagnosticsViewModel
{
    private readonly RuntimeDiagnosticService _diagnostics;

    public DiagnosticsViewModel(RuntimeDiagnosticService diagnostics)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        Entries = new ObservableCollection<RuntimeDiagnosticEntry>();
    }

    public ObservableCollection<RuntimeDiagnosticEntry> Entries { get; }

    public void RefreshSnapshot()
    {
        try
        {
            SynchronizeEntries(GetOrderedSnapshot());
        }
        catch
        {
            // A diagnostics UI refresh must not become an application failure.
        }
    }

    public void Clear()
    {
        try
        {
            _diagnostics.Clear();
            Entries.Clear();
        }
        catch
        {
            // A diagnostics UI action must not become an application failure.
        }
    }

    public string CreateCopyText()
    {
        try
        {
            return DiagnosticsLogFileService.Format(GetOrderedSnapshot());
        }
        catch
        {
            return string.Empty;
        }
    }

    public IReadOnlyList<RuntimeDiagnosticEntry> GetSnapshot() => GetOrderedSnapshot();

    private IReadOnlyList<RuntimeDiagnosticEntry> GetOrderedSnapshot() =>
        _diagnostics.GetSnapshot()
            .Select((entry, index) => new { Entry = entry, Index = index })
            .OrderBy(item => item.Entry.Timestamp)
            .ThenBy(item => item.Entry.Sequence)
            .ThenBy(item => item.Index)
            .Select(item => item.Entry)
            .ToArray();

    private void SynchronizeEntries(IReadOnlyList<RuntimeDiagnosticEntry> snapshot)
    {
        if (HasSameReferences(snapshot))
            return;

        var snapshotEntries = new HashSet<RuntimeDiagnosticEntry>(snapshot, ReferenceEqualityComparer.Instance);
        for (var index = Entries.Count - 1; index >= 0; index--)
        {
            if (!snapshotEntries.Contains(Entries[index]))
                Entries.RemoveAt(index);
        }

        for (var index = 0; index < snapshot.Count; index++)
        {
            if (index < Entries.Count && ReferenceEquals(Entries[index], snapshot[index]))
                continue;

            var existingIndex = FindReferenceIndex(snapshot[index]);
            if (existingIndex >= 0)
            {
                var entry = Entries[existingIndex];
                Entries.RemoveAt(existingIndex);
                Entries.Insert(index, entry);
            }
            else
            {
                Entries.Insert(index, snapshot[index]);
            }
        }
    }

    private bool HasSameReferences(IReadOnlyList<RuntimeDiagnosticEntry> snapshot)
    {
        if (Entries.Count != snapshot.Count)
            return false;

        for (var index = 0; index < snapshot.Count; index++)
        {
            if (!ReferenceEquals(Entries[index], snapshot[index]))
                return false;
        }

        return true;
    }

    private int FindReferenceIndex(RuntimeDiagnosticEntry entry)
    {
        for (var index = 0; index < Entries.Count; index++)
        {
            if (ReferenceEquals(Entries[index], entry))
                return index;
        }

        return -1;
    }
}
