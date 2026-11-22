using WurmMiningCounter.MiningStatistics;

namespace WurmMiningCounter.Tests.MiningStatistics;

public sealed class MiningStatisticsSessionBaselineStoreTests
{
    [Fact]
    public void MissingProfile_HasZeroBaseline()
    {
        var store = new MiningStatisticsSessionBaselineStore();

        Assert.Equal(0, store.Get(EventTransportTestProfileIds.Alice).MiningTickBaseline);
        Assert.Equal(0m, store.Get(EventTransportTestProfileIds.Alice).MiningGainBaseline);
        Assert.Null(store.Get(EventTransportTestProfileIds.Alice).SessionStartedAtUtc);
    }

    [Fact]
    public void SessionCapture_StoresZeroStateAndTimestamp()
    {
        var store = new MiningStatisticsSessionBaselineStore();
        var startedAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 0, 0m, startedAt);

        Assert.Equal(new MiningStatisticsSessionBaselines(0, 0m, startedAt), store.Get(EventTransportTestProfileIds.Alice));
    }

    [Fact]
    public void Capture_StoresAllSessionBoundaryComponents()
    {
        var store = new MiningStatisticsSessionBaselineStore();
        var startedAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 12, 0.123456m, startedAt);

        Assert.Equal(new MiningStatisticsSessionBaselines(12, 0.123456m, startedAt), store.Get(EventTransportTestProfileIds.Alice));
    }

    [Fact]
    public void SecondCapture_ReplacesPreviousBaselineWithoutHistory()
    {
        var store = new MiningStatisticsSessionBaselineStore();
        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 12, 0.01m, DateTimeOffset.UnixEpoch);

        var replacement = new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero);
        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 14, 0.02m, replacement);

        Assert.Equal(new MiningStatisticsSessionBaselines(14, 0.02m, replacement), store.Get(EventTransportTestProfileIds.Alice));
    }

    [Fact]
    public void Profiles_AreIndependent()
    {
        var store = new MiningStatisticsSessionBaselineStore();
        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 10, 0.01m, DateTimeOffset.UnixEpoch);
        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Bob, 20, 0.02m, DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.Equal(10, store.Get(EventTransportTestProfileIds.Alice).MiningTickBaseline);
        Assert.Equal(20, store.Get(EventTransportTestProfileIds.Bob).MiningTickBaseline);
    }

    [Fact]
    public void Capture_RejectsNegativeRawTicks()
    {
        var store = new MiningStatisticsSessionBaselineStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, -1, 0m, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 0, -0.001m, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void BaselineChanged_RaisesOnlyWhenBaselineChanges()
    {
        var store = new MiningStatisticsSessionBaselineStore();
        var profiles = new List<PlayerProfileId>();
        store.BaselineChanged += (_, args) => profiles.Add(args.ProfileId);

        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 0, 0m, DateTimeOffset.UnixEpoch);
        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 3, 0m, DateTimeOffset.UnixEpoch);
        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 3, 0m, DateTimeOffset.UnixEpoch);
        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Bob, 4, 0m, DateTimeOffset.UnixEpoch);
        store.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 0, 0.001m, DateTimeOffset.UnixEpoch);

        Assert.Equal([EventTransportTestProfileIds.Alice, EventTransportTestProfileIds.Alice, EventTransportTestProfileIds.Bob, EventTransportTestProfileIds.Alice], profiles);
        Assert.Equal(0, store.Get(EventTransportTestProfileIds.Alice).MiningTickBaseline);
    }

    [Fact]
    public void SameNameProfiles_KeepIndependentSessionBaselines()
    {
        var first = PlayerProfileId.CreateNew(); var second = PlayerProfileId.CreateNew(); var store = new MiningStatisticsSessionBaselineStore();
        store.CaptureMiningSessionBaseline(first, 3, 0.003m, DateTimeOffset.UnixEpoch);
        store.CaptureMiningSessionBaseline(second, 7, 0.007m, DateTimeOffset.UnixEpoch.AddMinutes(1));
        store.CaptureMiningSessionBaseline(first, 4, 0.004m, DateTimeOffset.UnixEpoch.AddMinutes(2));
        Assert.Equal(4, store.Get(first).MiningTickBaseline); Assert.Equal(0.004m, store.Get(first).MiningGainBaseline);
        Assert.Equal(7, store.Get(second).MiningTickBaseline); Assert.Equal(0.007m, store.Get(second).MiningGainBaseline);
    }
}
