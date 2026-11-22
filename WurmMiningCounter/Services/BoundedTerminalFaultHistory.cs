using System.Runtime.CompilerServices;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

/// <summary>
/// Keeps enough terminal-fault evidence for shutdown diagnostics without making
/// a long-running monitor retain every completed failure forever.
/// </summary>
internal sealed class BoundedTerminalFaultHistory<T>
{
    internal const int RecentCapacity = 32;

    private readonly Func<T, Exception> _getException;
    private readonly Queue<T> _recent = [];
    private T? _first;
    private long _omittedCount;

    public BoundedTerminalFaultHistory(Func<T, Exception> getException)
    {
        _getException = getException ?? throw new ArgumentNullException(nameof(getException));
    }

    internal int RetainedCount => (_first is null ? 0 : 1) + _recent.Count;
    internal long OmittedCount => _omittedCount;

    internal void Add(T entry)
    {
        var exception = _getException(entry);
        if (_first is not null && ReferenceEquals(_getException(_first), exception))
            return;
        if (_recent.Any(existing => ReferenceEquals(_getException(existing), exception)))
            return;

        if (_first is null)
        {
            _first = entry;
            return;
        }

        if (_recent.Count == RecentCapacity)
        {
            _recent.Dequeue();
            _omittedCount++;
        }
        _recent.Enqueue(entry);
    }

    internal IReadOnlyList<T> Snapshot()
    {
        if (_first is null)
            return [];

        var result = new T[1 + _recent.Count];
        result[0] = _first;
        _recent.CopyTo(result, 1);
        return result;
    }

    internal Exception? CreateOmittedSummary(string scope) => _omittedCount == 0
        ? null
        : new TerminalFaultHistorySummaryException(scope, _omittedCount);
}

/// <summary>Numeric shutdown evidence for terminal faults no longer retained in detail.</summary>
internal sealed class TerminalFaultHistorySummaryException(string scope, long omittedCount)
    : Exception($"{omittedCount} earlier terminal fault(s) in {scope} were omitted from detailed history.")
{
    internal long OmittedCount { get; } = omittedCount;
}

/// <summary>
/// Correlates a dispatcher terminal status with the same release exception
/// without owning that exception. Recent weak entries make its bounded state
/// observable; the weak table preserves exact-instance matching while the
/// release task itself still owns the failure.
/// </summary>
internal sealed class BoundedDispatcherFaultCorrelationCache
{
    private const int RecentCapacity = 32;
    private ConditionalWeakTable<Exception, Correlation> _byException = new();
    private readonly Dictionary<(PlayerProfileId ProfileId, EventModule Module), RecentEntry> _recent = [];
    private readonly LinkedList<(PlayerProfileId ProfileId, EventModule Module)> _order = [];

    internal int Count => _recent.Count;

    internal void Add(EventModuleTerminalStatus status)
    {
        _byException.Remove(status.Failure);
        _byException.Add(status.Failure, new Correlation(status.ProfileId, status.Module));
        var key = (status.ProfileId, status.Module);
        if (_recent.TryGetValue(key, out var existing))
        {
            _order.Remove(existing.OrderNode);
        }
        else if (_recent.Count == RecentCapacity)
        {
            var oldest = _order.First!;
            _recent.Remove(oldest.Value);
            _order.RemoveFirst();
        }
        var orderNode = _order.AddLast(key);
        _recent[key] = new RecentEntry(new WeakReference<Exception>(status.Failure), orderNode);
    }

    internal bool TryConsume(PlayerProfileId profileId, Exception failure)
    {
        foreach (var leaf in Flatten(failure))
        {
            if (!_byException.TryGetValue(leaf, out var correlation) || correlation.ProfileId != profileId)
                continue;

            _byException.Remove(leaf);
            var key = (correlation.ProfileId, correlation.Module);
            if (_recent.TryGetValue(key, out var recent)
                && recent.Failure.TryGetTarget(out var current)
                && ReferenceEquals(current, leaf))
            {
                _recent.Remove(key);
                _order.Remove(recent.OrderNode);
            }
            return true;
        }

        return false;
    }

    internal void Clear()
    {
        _byException = new ConditionalWeakTable<Exception, Correlation>();
        _recent.Clear();
        _order.Clear();
    }

    private static IEnumerable<Exception> Flatten(Exception failure) => failure is AggregateException aggregate
        ? aggregate.Flatten().InnerExceptions
        : [failure];

    private sealed record Correlation(PlayerProfileId ProfileId, EventModule Module);
    private sealed record RecentEntry(
        WeakReference<Exception> Failure,
        LinkedListNode<(PlayerProfileId ProfileId, EventModule Module)> OrderNode);
}
