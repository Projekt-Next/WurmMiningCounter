using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventAlertPopupHostTests
{
    [Fact]
    public void FirstAlertShowsImmediately()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);

        host.Enqueue(Notification("first"));

        Assert.Equal(["first"], factory.CreatedTexts);
        Assert.Equal(1, factory.Popups[0].ShowCalls);
        Assert.Equal(1, timer.StartCalls);
    }

    [Fact]
    public void LaterAlertsWaitInFifoOrderUntilActivePopupCloses()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);

        host.Enqueue(Notification("first"));
        host.Enqueue(Notification("second"));
        host.Enqueue(Notification("third"));

        Assert.Equal(["first"], factory.CreatedTexts);
        timer.Fire();
        Assert.Equal(["first", "second"], factory.CreatedTexts);
        timer.Fire();
        Assert.Equal(["first", "second", "third"], factory.CreatedTexts);
    }

    [Fact]
    public void ClosingActivePopupShowsNextQueuedAlert()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);
        host.Enqueue(Notification("first"));
        host.Enqueue(Notification("second"));

        factory.Popups[0].Close();

        Assert.Equal(["first", "second"], factory.CreatedTexts);
        Assert.True(factory.Popups[1].Visible);
    }

    [Fact]
    public void OnlyOnePopupIsVisibleAtATime()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);

        host.Enqueue(Notification("first"));
        host.Enqueue(Notification("second"));

        Assert.Single(factory.Popups);
        Assert.True(factory.Popups[0].Visible);
        timer.Fire();

        Assert.False(factory.Popups[0].Visible);
        Assert.True(factory.Popups[1].Visible);
    }

    [Fact]
    public void DisposeClearsPendingAlertsAndClosesActivePopup()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        var host = CreateHost(factory, timer);
        host.Enqueue(Notification("first"));
        host.Enqueue(Notification("second"));

        host.Dispose();

        Assert.Equal(1, factory.Popups[0].CloseCalls);
        Assert.Single(factory.Popups);
        Assert.True(timer.StopCalls >= 1);
    }

    [Fact]
    public void DisposeOnUiThreadClosesActivePopupClearsPendingAndDoesNotShowNext()
    {
        var poster = new QueuedPoster();
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = new EventAlertPopupHost(poster.Post, factory, timer, () => true);
        host.Enqueue(Notification("active"));
        poster.RunOne();
        host.Enqueue(Notification("pending"));

        host.Dispose();

        Assert.Single(factory.Popups);
        Assert.Equal(1, factory.Popups[0].CloseCalls);
        Assert.Equal(0, host.PendingCount);
        Assert.Equal(1, timer.StopCalls);
        Assert.Equal(0, poster.Count);
    }

    [Fact]
    public void LateClosedAndTimerCallbacksAfterDisposeDoNotReactivateHost()
    {
        var poster = new QueuedPoster();
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = new EventAlertPopupHost(poster.Post, factory, timer, () => true);
        host.Enqueue(Notification("active"));
        poster.RunOne();
        host.Enqueue(Notification("pending"));
        host.Dispose();

        factory.Popups[0].RaiseClosed();
        timer.Fire();

        Assert.Single(factory.Popups);
        Assert.Equal(1, factory.Popups[0].CloseCalls);
        Assert.Equal(0, host.PendingCount);
        Assert.Equal(0, poster.Count);
    }

    [Fact]
    public void RepeatedDisposeIsSafeAndDoesNotScheduleAdditionalCleanup()
    {
        var poster = new QueuedPoster();
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        var host = new EventAlertPopupHost(poster.Post, factory, timer, () => true);
        host.Enqueue(Notification("active"));
        poster.RunOne();

        host.Dispose();
        host.Dispose();
        host.Dispose();

        Assert.Equal(1, factory.Popups[0].CloseCalls);
        Assert.Equal(1, timer.StopCalls);
        Assert.Equal(0, poster.Count);
    }

    [Fact]
    public void DeliveryAfterPopupHostShutdownCannotCreateANewPopup()
    {
        var poster = new QueuedPoster();
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = new EventAlertPopupHost(poster.Post, factory, timer, () => true);
        using var delivery = new EventAlertDelivery(
            new ExternalSoundService(Path.GetTempPath(), _ => { }),
            host.Enqueue);
        host.Dispose();

        delivery.Deliver(Notification("late"));
        delivery.Dispose();

        Assert.Empty(factory.Popups);
        Assert.Equal(0, poster.Count);
    }

    [Fact]
    public void EnqueueAfterDisposeIsIgnored()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);
        host.Dispose();

        host.Enqueue(Notification("ignored"));

        Assert.Empty(factory.Popups);
    }

    [Fact]
    public void ShowAndCloseFailuresDoNotEscapeDelivery()
    {
        var showFailureFactory = new FakePopupFactory { ThrowOnShow = true };
        var showFailureTimer = new FakeTimer();
        using var showFailureHost = CreateHost(showFailureFactory, showFailureTimer);
        var showFailure = Record.Exception(() => Deliver(showFailureHost, Notification("show")));

        Assert.Null(showFailure);

        var closeFailureFactory = new FakePopupFactory { ThrowOnClose = true };
        var closeFailureTimer = new FakeTimer();
        using var closeFailureHost = CreateHost(closeFailureFactory, closeFailureTimer);
        Deliver(closeFailureHost, Notification("close"));
        var closeFailure = Record.Exception(closeFailureTimer.Fire);

        Assert.Null(closeFailure);
    }

    [Fact]
    public void FactoryCreateFailureDoesNotBlockTheNextPendingPopup()
    {
        var factory = new FakePopupFactory { CreateFailuresRemaining = 1 };
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);

        host.Enqueue(Notification("failed"));
        host.Enqueue(Notification("shown"));

        Assert.Equal(["shown"], factory.CreatedTexts);
        Assert.True(factory.Popups[0].Visible);
    }

    [Fact]
    public void TimerStartFailureDoesNotBlockTheNextPendingPopup()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer { StartFailuresRemaining = 1 };
        using var host = CreateHost(factory, timer);

        host.Enqueue(Notification("failed"));
        host.Enqueue(Notification("shown"));

        Assert.Equal(["failed", "shown"], factory.CreatedTexts);
        Assert.Equal(2, timer.StartCalls);
        Assert.True(factory.Popups[1].Visible);
    }

    [Fact]
    public void TimerStopFailureDoesNotBlockTheNextPendingPopup()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer { StopFailuresRemaining = 1 };
        using var host = CreateHost(factory, timer);
        host.Enqueue(Notification("first"));
        host.Enqueue(Notification("second"));

        timer.Fire();

        Assert.Equal(["first", "second"], factory.CreatedTexts);
        Assert.True(factory.Popups[1].Visible);
    }

    [Fact]
    public void CloseFailureStillAdvancesToTheNextPendingPopup()
    {
        var factory = new FakePopupFactory { CloseFailuresForFirstPopup = 2 };
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);
        host.Enqueue(Notification("first"));
        host.Enqueue(Notification("second"));

        timer.Fire();

        Assert.Equal(["first", "second"], factory.CreatedTexts);
        Assert.True(factory.Popups[1].Visible);
    }

    [Fact]
    public void StaleAndDoubleClosedCallbacksCannotAdvanceTheWrongPopup()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);
        host.Enqueue(Notification("first"));
        host.Enqueue(Notification("second"));
        host.Enqueue(Notification("third"));

        factory.Popups[0].Close();
        factory.Popups[0].RaiseClosed();
        Assert.Equal(["first", "second"], factory.CreatedTexts);

        factory.Popups[1].RaiseClosed();
        factory.Popups[1].RaiseClosed();

        Assert.Equal(["first", "second", "third"], factory.CreatedTexts);
    }

    [Fact]
    public void QueuedUiCallbackAfterDisposeCannotCreateAPopup()
    {
        var poster = new QueuedPoster();
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = new EventAlertPopupHost(poster.Post, factory, timer, () => true);
        host.Enqueue(Notification("queued"));

        host.Dispose();
        poster.RunOne();

        Assert.Empty(factory.Popups);
        Assert.Equal(0, host.PendingCount);
    }

    [Fact]
    public void PendingBacklogIsBoundedAndDropsOldestPendingAlert()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);
        host.Enqueue(Notification("active"));
        for (var index = 1; index <= EventAlertPopupHost.PendingCapacity + 1; index++)
            host.Enqueue(Notification(index.ToString()));

        Assert.Equal(EventAlertPopupHost.PendingCapacity, host.PendingCount);
        timer.Fire();
        Assert.Equal("2", factory.CreatedTexts[1]);
    }

    [Fact]
    public void PopupOverloadRetainsTheNewestPendingNotification()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);
        host.Enqueue(Notification("active"));
        for (var index = 1; index <= EventAlertPopupHost.PendingCapacity + 1; index++)
            host.Enqueue(Notification(index.ToString()));

        for (var index = 0; index < EventAlertPopupHost.PendingCapacity; index++)
            timer.Fire();

        Assert.Equal((EventAlertPopupHost.PendingCapacity + 1).ToString(), factory.CreatedTexts[^1]);
    }

    [Fact]
    public void BurstShowsAtMostActivePlusTwoNewestPendingPopupsInFifoOrder()
    {
        var factory = new FakePopupFactory();
        var timer = new FakeTimer();
        using var host = CreateHost(factory, timer);
        host.Enqueue(Notification("active"));
        host.Enqueue(Notification("oldest-pending"));
        host.Enqueue(Notification("retained-first"));
        host.Enqueue(Notification("retained-last"));

        Assert.Equal(2, host.PendingCount);
        timer.Fire();
        timer.Fire();

        Assert.Equal(["active", "retained-first", "retained-last"], factory.CreatedTexts);
        Assert.Equal(3, factory.Popups.Count);
    }

    [Fact]
    public void EnqueueAfterIdleFailureDrainSchedulesOneFreshUiWakeup()
    {
        var poster = new QueuedPoster();
        var factory = new FakePopupFactory { ThrowOnShow = true };
        var timer = new FakeTimer();
        using var host = new EventAlertPopupHost(poster.Post, factory, timer);
        host.Enqueue(Notification("first"));
        poster.RunOne();
        Assert.Equal(0, poster.Count);

        host.Enqueue(Notification("second"));

        Assert.Equal(1, poster.Count);
        poster.RunOne();
        Assert.Equal(["first", "second"], factory.CreatedTexts);
    }

    [Fact]
    public void FloodPostsOnlyOneUiWakeupAndPersistentShowFailureDrainsIteratively()
    {
        var poster = new QueuedPoster();
        var factory = new FakePopupFactory { ThrowOnShow = true };
        var timer = new FakeTimer();
        using var host = new EventAlertPopupHost(poster.Post, factory, timer);
        for (var index = 0; index < EventAlertPopupHost.PendingCapacity + 3; index++)
            host.Enqueue(Notification(index.ToString()));

        Assert.Equal(1, poster.Count);
        poster.RunOne();
        Assert.Equal(EventAlertPopupHost.PendingCapacity, factory.Popups.Count);
        Assert.Equal(0, host.PendingCount);
    }

    [Fact]
    public void PersistentShowFailureYieldsAfterBoundedWorkWhenProducerReplenishesPending()
    {
        var poster = new QueuedPoster();
        var factory = new FakePopupFactory { ThrowOnShow = true };
        var timer = new FakeTimer();
        using var host = new EventAlertPopupHost(poster.Post, factory, timer);
        var replenished = 0;
        factory.OnShow = () =>
        {
            if (replenished >= EventAlertPopupHost.MaximumAttemptsPerUiCallback)
                return;

            host.Enqueue(Notification($"replenished-{replenished++}"));
        };

        for (var index = 0; index < EventAlertPopupHost.PendingCapacity; index++)
            host.Enqueue(Notification($"initial-{index}"));

        Assert.Equal(1, poster.Count);
        poster.RunOne();

        Assert.Equal(EventAlertPopupHost.MaximumAttemptsPerUiCallback, factory.Popups.Count);
        Assert.Equal(1, poster.Count);
        Assert.Equal(EventAlertPopupHost.PendingCapacity, host.PendingCount);
        Assert.Equal(2, poster.PostCount);
        Assert.Equal(
            Enumerable.Range(0, EventAlertPopupHost.MaximumAttemptsPerUiCallback).Select(index => $"initial-{index}"),
            factory.CreatedTexts);

        poster.RunOne();

        Assert.Equal(EventAlertPopupHost.MaximumAttemptsPerUiCallback * 2, factory.Popups.Count);
        Assert.Equal(0, host.PendingCount);
        Assert.Equal(2, poster.PostCount);
        Assert.Equal(
            Enumerable.Range(0, EventAlertPopupHost.MaximumAttemptsPerUiCallback).Select(index => $"replenished-{index}"),
            factory.CreatedTexts.Skip(EventAlertPopupHost.MaximumAttemptsPerUiCallback));
    }

    private static EventAlertPopupHost CreateHost(FakePopupFactory factory, FakeTimer timer) =>
        new(action => action(), factory, timer);

    private static void Deliver(EventAlertPopupHost host, EventAlertNotification notification)
    {
        var sounds = new ExternalSoundService(Path.GetTempPath(), _ => { });
        var delivery = new EventAlertDelivery(sounds, host.Enqueue, action => action());
        delivery.Deliver(notification);
    }

    private static EventAlertNotification Notification(string popupText) => new(
        "Alice",
        new EventAlertTrigger(
            Guid.NewGuid(),
            popupText,
            popupText,
            Enabled: true,
            null,
            popupText,
            IsBuiltIn: false));

    private sealed class FakePopupFactory : IEventAlertPopupFactory
    {
        public List<FakePopup> Popups { get; } = [];

        public IReadOnlyList<string> CreatedTexts => Popups.Select(popup => popup.Notification.Trigger.PopupText).ToArray();

        public bool ThrowOnShow { get; init; }

        public bool ThrowOnClose { get; init; }

        public int CreateFailuresRemaining { get; set; }

        public int CloseFailuresForFirstPopup { get; init; }

        public Action? OnShow { get; set; }

        public IEventAlertPopup Create(EventAlertNotification notification)
        {
            if (CreateFailuresRemaining > 0)
            {
                CreateFailuresRemaining--;
                throw new InvalidOperationException("Create failed.");
            }

            var closeFailuresRemaining = Popups.Count == 0 ? CloseFailuresForFirstPopup : 0;
            var popup = new FakePopup(notification, ThrowOnShow, ThrowOnClose, closeFailuresRemaining, OnShow);
            Popups.Add(popup);
            return popup;
        }
    }

    private sealed class FakePopup : IEventAlertPopup
    {
        private readonly bool _throwOnShow;
        private readonly bool _throwOnClose;
        private int _closeFailuresRemaining;
        private readonly Action? _onShow;

        public FakePopup(EventAlertNotification notification, bool throwOnShow, bool throwOnClose, int closeFailuresRemaining, Action? onShow)
        {
            Notification = notification;
            _throwOnShow = throwOnShow;
            _throwOnClose = throwOnClose;
            _closeFailuresRemaining = closeFailuresRemaining;
            _onShow = onShow;
        }

        public event Action? Closed;

        public EventAlertNotification Notification { get; }

        public int ShowCalls { get; private set; }

        public int CloseCalls { get; private set; }

        public bool Visible { get; private set; }

        public void Show()
        {
            ShowCalls++;
            _onShow?.Invoke();
            if (_throwOnShow)
                throw new InvalidOperationException("Show failed.");
            Visible = true;
        }

        public void Close()
        {
            CloseCalls++;
            if (_throwOnClose || _closeFailuresRemaining-- > 0)
                throw new InvalidOperationException("Close failed.");
            Visible = false;
            Closed?.Invoke();
        }

        public void RaiseClosed() => Closed?.Invoke();
    }

    private sealed class FakeTimer : IEventAlertPopupTimer
    {
        public event Action? Elapsed;

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public int StartFailuresRemaining { get; set; }

        public int StopFailuresRemaining { get; set; }

        public void Start()
        {
            StartCalls++;
            if (StartFailuresRemaining-- > 0)
                throw new InvalidOperationException("Start failed.");
        }

        public void Stop()
        {
            StopCalls++;
            if (StopFailuresRemaining-- > 0)
                throw new InvalidOperationException("Stop failed.");
        }

        public void Fire() => Elapsed?.Invoke();
    }

    private sealed class QueuedPoster
    {
        private readonly Queue<Action> _actions = new();
        public int Count => _actions.Count;
        public int PostCount { get; private set; }
        public void Post(Action action)
        {
            PostCount++;
            _actions.Enqueue(action);
        }
        public void RunOne() => _actions.Dequeue()();
    }
}
