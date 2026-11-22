using System.Text;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class RuntimeDiagnosticService
{
    public const int MaximumEntries = 1000;
    public const int MaximumMessageBytes = 8 * 1024;

    private readonly object _sync = new();
    private readonly Queue<RuntimeDiagnosticEntry> _entries = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private int _enabled;
    private long _nextSequence;

    public RuntimeDiagnosticService(bool enabled = true, Func<DateTimeOffset>? utcNow = null)
    {
        _enabled = enabled ? 1 : 0;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    public int EntryCount
    {
        get
        {
            try
            {
                lock (_sync)
                    return _entries.Count;
            }
            catch
            {
                return 0;
            }
        }
    }

    public void Report(RuntimeDiagnosticLevel level, string? source, string? message)
    {
        try
        {
            if (!Enabled)
                return;

            var timestamp = _utcNow().ToUniversalTime();
            var normalizedSource = string.IsNullOrWhiteSpace(source) ? "Unknown" : source;
            var truncatedMessage = TruncateMessage(message);

            lock (_sync)
            {
                if (!Enabled)
                    return;

                var entry = new RuntimeDiagnosticEntry(
                    timestamp,
                    level,
                    normalizedSource,
                    truncatedMessage,
                    ++_nextSequence);

                if (_entries.Count == MaximumEntries)
                    _entries.Dequeue();

                _entries.Enqueue(entry);
            }
        }
        catch
        {
            // Diagnostics must never affect the subsystem that reports them.
        }
    }

    public void Report(RuntimeDiagnosticLevel level, string? source, string format, params object?[] arguments)
    {
        try
        {
            Report(level, source, string.Format(format, arguments));
        }
        catch
        {
            // Diagnostics must never throw into the caller.
        }
    }

    public void ReportProfile(
        RuntimeDiagnosticLevel level,
        string? source,
        string? profileName,
        string? eventName,
        string? details)
    {
        try
        {
            Report(
                level,
                source,
                $"{profileName ?? "Unknown"} | {eventName ?? "Unknown"} | {details ?? string.Empty}");
        }
        catch
        {
            // Diagnostics must never throw into the caller.
        }
    }

    public IReadOnlyList<RuntimeDiagnosticEntry> GetSnapshot()
    {
        try
        {
            lock (_sync)
            {
                return _entries.ToArray();
            }
        }
        catch
        {
            return [];
        }
    }

    public void Clear()
    {
        try
        {
            lock (_sync)
            {
                _entries.Clear();
            }
        }
        catch
        {
            // Diagnostics must never throw into the caller.
        }
    }

    private static string TruncateMessage(string? message)
    {
        var value = message ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(value) <= MaximumMessageBytes)
            return value;

        var length = value.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > MaximumMessageBytes)
            length--;

        return value[..length];
    }
}
