using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

/// <summary>Bounded, fault-isolated FIFO delivery for Event alerts.</summary>
public sealed class EventAlertDelivery : IDisposable
{
    internal const int PendingCapacity = 64;

    private readonly object _sync = new();
    private readonly ExternalSoundService _sounds;
    private readonly Action<EventAlertNotification> _popupSink;
    private readonly Queue<EventAlertNotification> _pending = new();
    private readonly Thread _worker;
    private readonly Action<Action>? _testScheduler;
    private bool _accepting = true;
    private bool _disposed;

    public EventAlertDelivery(ExternalSoundService sounds, Action<EventAlertNotification> popupSink)
    {
        _sounds = sounds ?? throw new ArgumentNullException(nameof(sounds));
        _popupSink = popupSink ?? throw new ArgumentNullException(nameof(popupSink));
        _worker = new Thread(Consume) { IsBackground = true, Name = "EventAlertDelivery" };
        _worker.Start();
    }

    internal EventAlertDelivery(ExternalSoundService sounds, Action<EventAlertNotification> popupSink, Action<Action> testScheduler)
    {
        _sounds = sounds ?? throw new ArgumentNullException(nameof(sounds));
        _popupSink = popupSink ?? throw new ArgumentNullException(nameof(popupSink));
        _testScheduler = testScheduler ?? throw new ArgumentNullException(nameof(testScheduler));
        _worker = new Thread(() => { });
    }

    public void Deliver(EventAlertNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (_testScheduler is not null)
        {
            try
            {
                if (notification.Trigger.Enabled && notification.Trigger.SoundEnabled
                    && !string.IsNullOrWhiteSpace(notification.Trigger.SoundFileName))
                {
                    _testScheduler(() => _sounds.Play(notification.Trigger.SoundFileName));
                }
                if (notification.Trigger.Enabled && notification.Trigger.PopupEnabled)
                    _testScheduler(() => _popupSink(notification));
            }
            catch { /* Alert delivery must not affect its caller. */ }
            return;
        }
        try
        {
            lock (_sync)
            {
                if (!_accepting)
                    return;
                if (_pending.Count == PendingCapacity)
                    _pending.Dequeue();
                _pending.Enqueue(notification);
                Monitor.Pulse(_sync);
            }
        }
        catch { /* Alert delivery must not affect its caller. */ }
    }

    public void Dispose()
    {
        if (_testScheduler is not null)
            return;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _accepting = false;
            Monitor.PulseAll(_sync);
        }

        if (!ReferenceEquals(Thread.CurrentThread, _worker))
            _worker.Join();
    }

    internal int PendingCount { get { lock (_sync) return _pending.Count; } }

    internal bool IsAccepting { get { lock (_sync) return _accepting; } }

    private void Consume()
    {
        while (true)
        {
            EventAlertNotification notification;
            lock (_sync)
            {
                while (_pending.Count == 0 && _accepting)
                    Monitor.Wait(_sync);
                if (_pending.Count == 0)
                    return;
                notification = _pending.Dequeue();
            }
            DeliverNotification(notification);
        }
    }

    private void DeliverNotification(EventAlertNotification notification)
    {
        if (notification.Trigger.Enabled && notification.Trigger.SoundEnabled
            && !string.IsNullOrWhiteSpace(notification.Trigger.SoundFileName))
        {
            try { _sounds.Play(notification.Trigger.SoundFileName); }
            catch { /* Sound failure must not block popup admission. */ }
        }
        if (notification.Trigger.Enabled && notification.Trigger.PopupEnabled)
        {
            try { _popupSink(notification); }
            catch { /* Popup admission failure must not stop delivery. */ }
        }
    }
}
