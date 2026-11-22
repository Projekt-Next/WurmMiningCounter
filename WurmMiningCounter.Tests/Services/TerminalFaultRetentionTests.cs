using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class TerminalFaultRetentionTests
{
    [Fact]
    public void HistoryKeepsFirstAndThirtyTwoMostRecentExceptionsAfterOneThousandFaults()
    {
        var history = new BoundedTerminalFaultHistory<Exception>(exception => exception);
        var failures = Enumerable.Range(0, 1000)
            .Select(index => new InvalidOperationException(index.ToString()))
            .ToArray();

        foreach (var failure in failures)
            history.Add(failure);

        var retained = history.Snapshot();
        Assert.Equal(33, history.RetainedCount);
        Assert.Equal(967, history.OmittedCount);
        Assert.Same(failures[0], retained[0]);
        Assert.Equal(failures.Skip(968), retained.Skip(1));
        var summary = Assert.IsType<TerminalFaultHistorySummaryException>(
            history.CreateOmittedSummary("test"));
        Assert.Equal(967, summary.OmittedCount);
        Assert.DoesNotContain(failures[1], retained);
    }

    [Fact]
    public void DispatcherCorrelationIsBoundedButStillMatchesExactEvictedFailureInstance()
    {
        var cache = new BoundedDispatcherFaultCorrelationCache();
        var statuses = Enumerable.Range(0, 1000).Select(index =>
        {
            var failure = new InvalidOperationException(index.ToString());
            var id = new PlayerProfileId(Guid.NewGuid());
            var status = new EventModuleTerminalStatus(EventModule.Mining, id, "P" + index, "P.txt", 1, failure);
            cache.Add(status);
            return status;
        }).ToArray();

        Assert.Equal(32, cache.Count);
        var first = statuses[0];
        Assert.True(cache.TryConsume(first.ProfileId, new AggregateException(first.Failure)));
        Assert.False(cache.TryConsume(first.ProfileId, new AggregateException(first.Failure)));
        Assert.True(cache.TryConsume(statuses[^1].ProfileId, statuses[^1].Failure));

        cache.Clear();
        Assert.Equal(0, cache.Count);
    }
}
