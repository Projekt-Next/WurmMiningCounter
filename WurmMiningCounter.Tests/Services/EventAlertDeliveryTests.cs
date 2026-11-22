using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventAlertDeliveryTests
{
    [Fact]
    public void FreshBuiltInsWithoutSoundFilesDeliverOnlyPopups()
    {
        using var sounds = new TestSounds();
        var soundCalls = 0;
        var popups = new List<EventAlertNotification>();
        var delivery = CreateDelivery(
            sounds.CreateService(_ => soundCalls++),
            popups.Add,
            action => action());

        delivery.Deliver(Notification(EventAlertBuiltIns.Create()[0]));
        delivery.Deliver(Notification(EventAlertBuiltIns.Create()[1]));

        Assert.Equal(0, soundCalls);
        Assert.Equal(2, popups.Count);
    }

    [Fact]
    public void NoneSoundSchedulesOnlyPopup()
    {
        using var sounds = new TestSounds();
        var soundCalls = 0;
        var popupCalls = 0;
        var scheduled = 0;
        var delivery = CreateDelivery(
            sounds.CreateService(_ => soundCalls++),
            _ => popupCalls++,
            action =>
            {
                scheduled++;
                action();
            });

        delivery.Deliver(Notification(CustomTrigger(null)));

        Assert.Equal(0, soundCalls);
        Assert.Equal(1, popupCalls);
        Assert.Equal(1, scheduled);
    }

    [Fact]
    public void SchedulesTheSelectedCustomSoundFile()
    {
        using var sounds = new TestSounds("custom.wav");
        string? playedFileName = null;
        var delivery = CreateDelivery(
            sounds.CreateService(path => playedFileName = Path.GetFileName(path)),
            _ => { },
            action => action());

        delivery.Deliver(Notification(CustomTrigger("custom.wav")));

        Assert.Equal("custom.wav", playedFileName);
    }

    [Fact]
    public void MissingSelectedSoundDoesNotBlockDelivery()
    {
        using var sounds = new TestSounds();
        var popupCalls = 0;
        var delivery = CreateDelivery(
            sounds.CreateService(_ => throw new InvalidOperationException("Missing sound must not play.")),
            _ => popupCalls++,
            action => action());

        var exception = Record.Exception(() => delivery.Deliver(Notification(CustomTrigger("missing.wav"))));

        Assert.Null(exception);
        Assert.Equal(1, popupCalls);
    }

    [Fact]
    public void DisabledSoundAndPopupAreIndependentlyRespected()
    {
        using var sounds = new TestSounds();
        var soundCalls = 0;
        var popupCalls = 0;
        var delivery = CreateDelivery(sounds.CreateService(_ => soundCalls++), _ => popupCalls++, action => action());
        var trigger = EventAlertBuiltIns.Create()[0] with { SoundFileName = "wall-break-soon.wav" };

        delivery.Deliver(Notification(trigger with { SoundEnabled = false }));
        Assert.Equal(0, soundCalls);
        Assert.Equal(1, popupCalls);

        delivery.Deliver(Notification(trigger with { PopupEnabled = false }));
        Assert.Equal(1, soundCalls);
        Assert.Equal(1, popupCalls);
    }

    [Fact]
    public void PopupReceivesProfileAndTrigger()
    {
        using var sounds = new TestSounds();
        EventAlertNotification? received = null;
        var trigger = CustomTrigger(null);
        var delivery = CreateDelivery(sounds.CreateService(_ => { }), notification => received = notification, action => action());

        delivery.Deliver(new EventAlertNotification("Alice", trigger));

        Assert.Equal(new EventAlertNotification("Alice", trigger), received);
    }

    [Fact]
    public void SoundFailureDoesNotBlockPopup()
    {
        using var sounds = new TestSounds();
        var popupCalls = 0;
        var delivery = CreateDelivery(
            sounds.CreateService(_ => throw new InvalidOperationException("Sound failed.")),
            _ => popupCalls++,
            action => action());

        delivery.Deliver(Notification(EventAlertBuiltIns.Create()[0] with { SoundFileName = "wall-break-soon.wav" }));

        Assert.Equal(1, popupCalls);
    }

    [Fact]
    public void PopupFailureDoesNotBlockSound()
    {
        using var sounds = new TestSounds();
        var soundCalls = 0;
        var delivery = CreateDelivery(
            sounds.CreateService(_ => soundCalls++),
            _ => throw new InvalidOperationException("Popup failed."),
            action => action());

        delivery.Deliver(Notification(EventAlertBuiltIns.Create()[0] with { SoundFileName = "wall-break-soon.wav" }));

        Assert.Equal(1, soundCalls);
    }

    [Fact]
    public void SchedulerFailureDoesNotEscape()
    {
        using var sounds = new TestSounds();
        var delivery = CreateDelivery(
            sounds.CreateService(_ => { }),
            _ => { },
            _ => throw new InvalidOperationException("Scheduler failed."));

        var exception = Record.Exception(() => delivery.Deliver(Notification(EventAlertBuiltIns.Create()[0])));

        Assert.Null(exception);
    }

    [Fact]
    public void DeliveryReturnsBeforeAudioOrPopupExecution()
    {
        using var sounds = new TestSounds();
        var scheduled = new Queue<Action>();
        var soundCalls = 0;
        var popupCalls = 0;
        var delivery = CreateDelivery(
            sounds.CreateService(_ => soundCalls++),
            _ => popupCalls++,
            scheduled.Enqueue);

        delivery.Deliver(Notification(EventAlertBuiltIns.Create()[0] with { SoundFileName = "wall-break-soon.wav" }));

        Assert.Equal(2, scheduled.Count);
        Assert.Equal(0, soundCalls);
        Assert.Equal(0, popupCalls);

        while (scheduled.TryDequeue(out var action))
            action();

        Assert.Equal(1, soundCalls);
        Assert.Equal(1, popupCalls);
    }

    [Fact]
    public void BoundedWorkerQueueDropsOldestPendingNotificationAndKeepsNewest()
    {
        using var sounds = new TestSounds("custom.wav");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var received = new List<string>();
        using var delivery = new EventAlertDelivery(
            sounds.CreateService(_ => { started.Set(); release.Wait(); }),
            notification => { lock (received) received.Add(notification.Trigger.PopupText); });

        delivery.Deliver(Notification(CustomTrigger("custom.wav") with { PopupText = "0" }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        for (var index = 1; index <= EventAlertDelivery.PendingCapacity + 1; index++)
            delivery.Deliver(Notification(CustomTrigger("custom.wav") with { PopupText = index.ToString() }));

        Assert.Equal(EventAlertDelivery.PendingCapacity, delivery.PendingCount);
        release.Set();
        delivery.Dispose();
        lock (received)
        {
            Assert.DoesNotContain("1", received);
            Assert.Equal((EventAlertDelivery.PendingCapacity + 1).ToString(), received[^1]);
        }
    }

    [Fact]
    public void WorkerContinuesAfterPopupFailureAndStopsAdmissionAfterDispose()
    {
        using var sounds = new TestSounds();
        using var delivered = new ManualResetEventSlim();
        var calls = 0;
        var delivery = new EventAlertDelivery(
            sounds.CreateService(_ => { }),
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("popup");
                delivered.Set();
            });
        delivery.Deliver(Notification(CustomTrigger(null)));
        delivery.Deliver(Notification(CustomTrigger(null)));
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(5)));
        delivery.Dispose();
        delivery.Deliver(Notification(CustomTrigger(null)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ProductionWorkerPreservesFifoAndDeliversSoundBeforePopup()
    {
        using var sounds = new TestSounds("custom.wav");
        using var firstSoundStarted = new ManualResetEventSlim();
        using var releaseFirstSound = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        var calls = new List<string>();
        var sync = new object();
        using var delivery = new EventAlertDelivery(
            sounds.CreateService(path =>
            {
                var name = Path.GetFileName(path);
                lock (sync) calls.Add($"sound:{name}");
                if (name == "custom.wav")
                {
                    firstSoundStarted.Set();
                    releaseFirstSound.Wait();
                }
            }),
            notification =>
            {
                lock (sync)
                {
                    calls.Add($"popup:{notification.Trigger.PopupText}");
                    if (calls.Count == 6)
                        completed.Set();
                }
            });

        delivery.Deliver(Notification(CustomTrigger("custom.wav") with { PopupText = "first" }));
        Assert.True(firstSoundStarted.Wait(TimeSpan.FromSeconds(5)));
        delivery.Deliver(Notification(CustomTrigger("wall-break-soon.wav") with { PopupText = "second" }));
        delivery.Deliver(Notification(CustomTrigger("vein-destroyed.wav") with { PopupText = "third" }));
        releaseFirstSound.Set();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        lock (sync)
        {
            Assert.Equal(
                ["sound:custom.wav", "popup:first", "sound:wall-break-soon.wav", "popup:second", "sound:vein-destroyed.wav", "popup:third"],
                calls);
        }
    }

    [Fact]
    public void ProductionWorkerContinuesAfterSoundAndPopupFailures()
    {
        using var sounds = new TestSounds("custom.wav");
        using var delivered = new ManualResetEventSlim();
        var soundCalls = 0;
        var popupCalls = 0;
        using var delivery = new EventAlertDelivery(
            sounds.CreateService(_ =>
            {
                if (Interlocked.Increment(ref soundCalls) == 1)
                    throw new InvalidOperationException("sound");
            }),
            _ =>
            {
                if (Interlocked.Increment(ref popupCalls) == 1)
                    throw new InvalidOperationException("popup");
                delivered.Set();
            });

        delivery.Deliver(Notification(CustomTrigger("custom.wav")));
        delivery.Deliver(Notification(CustomTrigger("custom.wav")));

        Assert.True(delivered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, soundCalls);
        Assert.Equal(2, popupCalls);
    }

    [Fact]
    public void ProductionWorkerDrainsAcceptedWorkDuringDispose()
    {
        using var sounds = new TestSounds("custom.wav");
        using var firstSoundStarted = new ManualResetEventSlim();
        using var releaseFirstSound = new ManualResetEventSlim();
        var received = new List<string>();
        var sync = new object();
        var delivery = new EventAlertDelivery(
            sounds.CreateService(_ =>
            {
                firstSoundStarted.Set();
                releaseFirstSound.Wait();
            }),
            notification => { lock (sync) received.Add(notification.Trigger.PopupText); });
        try
        {
            delivery.Deliver(Notification(CustomTrigger("custom.wav") with { PopupText = "first" }));
            Assert.True(firstSoundStarted.Wait(TimeSpan.FromSeconds(5)));
            delivery.Deliver(Notification(CustomTrigger(null) with { PopupText = "second" }));

            var disposer = new Thread(delivery.Dispose);
            disposer.Start();
            releaseFirstSound.Set();
            Assert.True(disposer.Join(TimeSpan.FromSeconds(5)));

            lock (sync)
                Assert.Equal(["first", "second"], received);
        }
        finally
        {
            releaseFirstSound.Set();
            delivery.Dispose();
        }
    }

    [Fact]
    public void ProductionWorkerRejectsEnqueueAfterDisposeClosesAdmission()
    {
        using var sounds = new TestSounds("custom.wav");
        using var firstSoundStarted = new ManualResetEventSlim();
        using var releaseFirstSound = new ManualResetEventSlim();
        var received = new List<string>();
        var sync = new object();
        var delivery = new EventAlertDelivery(
            sounds.CreateService(_ =>
            {
                firstSoundStarted.Set();
                releaseFirstSound.Wait();
            }),
            notification => { lock (sync) received.Add(notification.Trigger.PopupText); });
        try
        {
            delivery.Deliver(Notification(CustomTrigger("custom.wav") with { PopupText = "first" }));
            Assert.True(firstSoundStarted.Wait(TimeSpan.FromSeconds(5)));
            delivery.Deliver(Notification(CustomTrigger(null) with { PopupText = "accepted" }));

            var disposer = new Thread(delivery.Dispose);
            disposer.Start();
            Assert.True(SpinWait.SpinUntil(() => !delivery.IsAccepting, TimeSpan.FromSeconds(5)));
            delivery.Deliver(Notification(CustomTrigger(null) with { PopupText = "rejected" }));
            releaseFirstSound.Set();
            Assert.True(disposer.Join(TimeSpan.FromSeconds(5)));

            lock (sync)
                Assert.Equal(["first", "accepted"], received);
        }
        finally
        {
            releaseFirstSound.Set();
            delivery.Dispose();
        }
    }

    private static EventAlertDelivery CreateDelivery(
        ExternalSoundService sounds,
        Action<EventAlertNotification> popupSink,
        Action<Action> schedule) =>
        new(sounds, popupSink, schedule);

    private static EventAlertNotification Notification(EventAlertTrigger trigger) =>
        new("Alice", trigger);

    private static EventAlertTrigger CustomTrigger(string? soundFileName) => new(
        Guid.NewGuid(), "Custom", "Custom", Enabled: true, soundFileName, "Custom", IsBuiltIn: false);

    private sealed class TestSounds : IDisposable
    {
        private readonly string _baseDirectory = Path.Combine(Path.GetTempPath(), "WurmMiningCounter.Tests", Guid.NewGuid().ToString("N"));

        public TestSounds(params string[] additionalSoundFileNames)
        {
            var soundsDirectory = Path.Combine(_baseDirectory, "Sounds");
            Directory.CreateDirectory(soundsDirectory);
            File.WriteAllText(Path.Combine(soundsDirectory, "wall-break-soon.wav"), string.Empty);
            File.WriteAllText(Path.Combine(soundsDirectory, "vein-destroyed.wav"), string.Empty);
            foreach (var soundFileName in additionalSoundFileNames)
                File.WriteAllText(Path.Combine(soundsDirectory, soundFileName), string.Empty);
        }

        public ExternalSoundService CreateService(Action<string> play) =>
            new(_baseDirectory, play);

        public void Dispose()
        {
            if (Directory.Exists(_baseDirectory))
                Directory.Delete(_baseDirectory, recursive: true);
        }
    }
}
