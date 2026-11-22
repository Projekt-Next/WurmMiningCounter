using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class SingleInstanceServiceTests
{
    [Fact]
    public void FirstInstanceIsPrimary()
    {
        using var primary = CreateService(() => { });

        Assert.True(primary.IsPrimary);
    }

    [Fact]
    public void SecondInstanceIsSecondary()
    {
        var names = CreateNames();
        using var primary = CreateService(names, () => { });
        using var secondary = CreateService(names, () => { });

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
    }

    [Fact]
    public async Task SecondarySendsActivateToPrimary()
    {
        var names = CreateNames();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var primary = CreateService(names, () => activated.TrySetResult());
        using var secondary = CreateService(names, () => { });

        Assert.True(secondary.SendActivate());
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OneActivateMessageInvokesExactlyOneCallback()
    {
        var names = CreateNames();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        using var primary = CreateService(names, () =>
        {
            Interlocked.Increment(ref callbackCount);
            activated.TrySetResult();
        });
        using var secondary = CreateService(names, () => { });

        Assert.True(secondary.SendActivate());
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public async Task UnknownMessageIsIgnored()
    {
        var names = CreateNames();
        var callbackCount = 0;
        using var primary = CreateService(names, () => Interlocked.Increment(ref callbackCount));
        using var secondary = CreateService(names, () => { });

        Assert.True(secondary.SendMessage("UNKNOWN"));
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public async Task SecondaryRetriesWhilePrimaryListenerStarts()
    {
        var names = CreateNames();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var primary = CreateService(
            names,
            () => activated.TrySetResult(),
            TimeSpan.FromMilliseconds(75));
        using var secondary = CreateService(names, () => { });

        Assert.True(secondary.SendActivate());
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        using var service = CreateService(() => { });

        service.Dispose();
        service.Dispose();

        Assert.False(service.IsPrimary);
    }

    [Fact]
    public async Task DisposePreventsFurtherActivateCallbacks()
    {
        var names = CreateNames();
        var callbackCount = 0;
        var primary = CreateService(names, () => Interlocked.Increment(ref callbackCount));
        using var secondary = CreateService(names, () => { });

        primary.Dispose();

        Assert.False(secondary.SendActivate());
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public void ReleasingPrimaryAllowsNextInstanceToBecomePrimary()
    {
        var names = CreateNames();
        var primary = CreateService(names, () => { });
        Assert.True(primary.IsPrimary);

        primary.Dispose();
        using var nextPrimary = CreateService(names, () => { });

        Assert.True(nextPrimary.IsPrimary);
    }

    private static SingleInstanceService CreateService(Action activate) =>
        CreateService(CreateNames(), activate);

    private static SingleInstanceService CreateService((string MutexName, string PipeName) names, Action activate) =>
        new(names.MutexName, names.PipeName, activate);

    private static SingleInstanceService CreateService(
        (string MutexName, string PipeName) names,
        Action activate,
        TimeSpan listenerStartupDelay) =>
        new(names.MutexName, names.PipeName, activate, listenerStartupDelay);

    private static (string MutexName, string PipeName) CreateNames()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return ($"Local\\WurmMiningCounter.Tests.{suffix}", $"WurmMiningCounter.Tests.{suffix}");
    }
}
