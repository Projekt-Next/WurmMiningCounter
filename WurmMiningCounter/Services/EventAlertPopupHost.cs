using System.Windows.Threading;
using WurmMiningCounter.Models;
using WurmMiningCounter.Views;

namespace WurmMiningCounter.Services;

/// <summary>UI-dispatched bounded FIFO host for independent Event alert popups.</summary>
public sealed class EventAlertPopupHost : IDisposable
{
    internal const int PendingCapacity = 2;
    internal const int MaximumAttemptsPerUiCallback = PendingCapacity;
    private static readonly TimeSpan DefaultPopupDuration = TimeSpan.FromSeconds(3);
    private readonly object _sync = new();
    private readonly Action<Action> _postToUi;
    private readonly IEventAlertPopupFactory _factory;
    private readonly IEventAlertPopupTimer _timer;
    private readonly Func<bool>? _isOnUiThread;
    private readonly Queue<EventAlertNotification> _pending = new();
    private IEventAlertPopup? _active;
    private Action? _activeClosedHandler;
    private bool _scheduled;
    private int _disposed;

    public EventAlertPopupHost(Dispatcher dispatcher, TimeSpan? popupDuration = null)
        : this(
            CreateUiPoster(dispatcher),
            new WpfEventAlertPopupFactory(),
            CreateTimer(dispatcher, popupDuration ?? DefaultPopupDuration),
            dispatcher.CheckAccess) { }

    internal EventAlertPopupHost(
        Action<Action> postToUi,
        IEventAlertPopupFactory factory,
        IEventAlertPopupTimer timer,
        Func<bool>? isOnUiThread = null)
    {
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _isOnUiThread = isOnUiThread;
        _timer.Elapsed += OnTimerElapsed;
    }

    public void Enqueue(EventAlertNotification? notification)
    {
        if (notification is null)
            return;
        var post = false;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (_pending.Count == PendingCapacity)
                _pending.Dequeue();
            _pending.Enqueue(notification);
            if (!_scheduled)
            {
                _scheduled = true;
                post = true;
            }
        }
        if (post)
            PostToUi(ProcessUi);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_sync)
            _pending.Clear();
        _timer.Elapsed -= OnTimerElapsed;
        if (_isOnUiThread?.Invoke() == true)
            CleanupDisposedUi();
        else
            PostToUi(CleanupDisposedUi);
    }

    internal int PendingCount { get { lock (_sync) return _pending.Count; } }

    private void ProcessUi()
    {
        for (var attempts = 0; attempts < MaximumAttemptsPerUiCallback; attempts++)
        {
            EventAlertNotification notification;
            lock (_sync)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    _scheduled = false;
                    return;
                }
                if (_active is not null)
                    return;
                if (_pending.Count == 0)
                {
                    _scheduled = false;
                    return;
                }
                notification = _pending.Dequeue();
            }

            IEventAlertPopup? popup = null;
            try
            {
                popup = _factory.Create(notification);
                _active = popup;
                _activeClosedHandler = () => OnPopupClosed(popup);
                popup.Closed += _activeClosedHandler;
                popup.Show();
                _timer.Start();
                return;
            }
            catch
            {
                FailActive(popup);
                // Keep failures iterative, but yield after a bounded number of attempts.
            }
        }

        var postContinuation = false;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _scheduled = false;
                return;
            }

            // _scheduled remains true while this callback hands its work to exactly
            // one continuation, so concurrent producers cannot post a duplicate wakeup.
            if (_active is null && _pending.Count > 0)
                postContinuation = true;
            else
                _scheduled = false;
        }

        if (postContinuation)
            PostToUi(ProcessUi);
    }

    private void OnTimerElapsed() => CloseActive();

    private void CloseActive()
    {
        if (Volatile.Read(ref _disposed) != 0 || _active is not { } active)
            return;
        TryStopTimer();
        try { active.Close(); }
        catch { FailActive(active); ProcessUi(); return; }
        if (ReferenceEquals(_active, active))
        {
            DetachActive(active);
            ProcessUi();
        }
    }

    private void OnPopupClosed(IEventAlertPopup popup)
    {
        if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(_active, popup))
            return;
        DetachActive(popup);
        ProcessUi();
    }

    private void FailActive(IEventAlertPopup? popup)
    {
        if (popup is not null)
        {
            if (ReferenceEquals(_active, popup) && _activeClosedHandler is not null)
                popup.Closed -= _activeClosedHandler;
            try { popup.Close(); }
            catch { /* Failed popup cleanup must not stop the queue. */ }
        }
        if (ReferenceEquals(_active, popup))
        {
            _active = null;
            _activeClosedHandler = null;
        }
        TryStopTimer();
    }

    private void DetachActive(IEventAlertPopup popup)
    {
        if (!ReferenceEquals(_active, popup))
            return;
        _active = null;
        if (_activeClosedHandler is not null)
            popup.Closed -= _activeClosedHandler;
        _activeClosedHandler = null;
        TryStopTimer();
    }

    private void CleanupDisposedUi()
    {
        lock (_sync)
            _scheduled = false;
        TryStopTimer();
        if (_active is not { } active)
            return;
        _active = null;
        if (_activeClosedHandler is not null)
            active.Closed -= _activeClosedHandler;
        _activeClosedHandler = null;
        try { active.Close(); }
        catch { /* UI cleanup must not throw. */ }
    }

    private void TryStopTimer()
    {
        try { _timer.Stop(); }
        catch { /* Timer cleanup must not throw. */ }
    }

    private void PostToUi(Action action)
    {
        try { _postToUi(action); }
        catch { lock (_sync) _scheduled = false; }
    }

    private static Action<Action> CreateUiPoster(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return action => _ = dispatcher.BeginInvoke(action);
    }

    private static IEventAlertPopupTimer CreateTimer(Dispatcher dispatcher, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return new DispatcherEventAlertPopupTimer(dispatcher, duration);
    }
}

internal interface IEventAlertPopup { event Action? Closed; void Show(); void Close(); }
internal interface IEventAlertPopupFactory { IEventAlertPopup Create(EventAlertNotification notification); }
internal interface IEventAlertPopupTimer { event Action? Elapsed; void Start(); void Stop(); }
internal sealed class WpfEventAlertPopupFactory : IEventAlertPopupFactory
{
    public IEventAlertPopup Create(EventAlertNotification notification) => new EventAlertPopupWindow(notification);
}
internal sealed class DispatcherEventAlertPopupTimer : IEventAlertPopupTimer
{
    private readonly DispatcherTimer _timer;
    public DispatcherEventAlertPopupTimer(Dispatcher dispatcher, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval };
        _timer.Tick += (_, _) => Elapsed?.Invoke();
    }
    public event Action? Elapsed;
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
