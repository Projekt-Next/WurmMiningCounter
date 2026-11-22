using System.Collections.Concurrent;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventAlertProductionWiringTests
{
    [Fact]
    public async Task PipelineDispatcherCoordinatorAndProductionAlertWiringDeliverBuiltInsAndCustomTriggers()
    {
        using var files = new EventFiles();
        using var settingsFiles = new SettingsFiles();
        var alicePath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", string.Empty);
        var bobPath = files.Add(EventTransportTestProfileIds.Bob, "Bob", "_Event.2026-08.txt", string.Empty);
        using var sounds = new TestSounds();
        var custom = new EventAlertTrigger(
            Guid.NewGuid(), "Custom", "Custom alert", Enabled: true,
            null, "Custom popup", IsBuiltIn: false);
        var settingsService = new SettingsService(settingsFiles.DirectoryPath);
        var persistedSettings = new AppSettings
        {
            EventAlerts =
            [
                EventAlertBuiltIns.Create()[0] with { SoundFileName = "wall-break-soon.wav" },
                EventAlertBuiltIns.Create()[1] with { SoundFileName = "vein-destroyed.wav" },
                custom
            ]
        };
        Assert.True(settingsService.Save(persistedSettings));
        var settings = settingsService.Load();
        var catalog = new EventAlertCatalog(settings.EventAlerts);
        var wallBreak = catalog.GetSnapshot().Single(trigger => trigger.Id == EventAlertBuiltIns.WallBreakId);
        Assert.True(wallBreak.Enabled);
        Assert.True(wallBreak.SoundEnabled);
        Assert.True(wallBreak.PopupEnabled);
        Assert.Equal("vein-destroyed.wav", wallBreak.SoundFileName);
        Assert.Equal("Wall Break", wallBreak.PopupText);
        var popups = new ConcurrentQueue<EventAlertNotification>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var soundPaths = new ConcurrentQueue<string>();
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        using var coordinator = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), coordinator);
        var alertWiring = App.WireEventAlerts(
            coordinator,
            catalog,
            sounds.CreateService(soundPaths.Enqueue),
            notification =>
            {
                popups.Enqueue(notification);
                if (popups.Count == 3)
                    delivered.TrySetResult();
            });
        using var alertDelivery = alertWiring.Delivery;
        var pipeline = new EventPipeline(
            files.PlayersPath,
            dispatcher.Handoff,
            new EventLogReader(),
            clock,
            files.Scan,
            cycleCompleted: cycles.Signal,
            releaseProfileWithIdentity: (profileId, _) => dispatcher.ReleaseProfile(profileId),
            profileIdentityResolver: files.Resolver);

        try
        {
            pipeline.Start();
            File.AppendAllText(alicePath, $"[12:00:00] {EventLogMessages.WallBreakSoon}\n[12:34:56] {custom.MatchText}\n");
            File.AppendAllText(bobPath, $"[12:00:00] {EventLogMessages.WallBreak}\n");
            await Step(clock, cycles, 1);
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(popups, notification =>
                notification.ProfileName == "Alice"
                && notification.Trigger.Id == EventAlertBuiltIns.WallBreakSoonId);
            Assert.Contains(popups, notification =>
                notification.ProfileName == "Bob"
                && notification.Trigger.Id == EventAlertBuiltIns.WallBreakId);
            Assert.Contains(popups, notification =>
                notification.ProfileName == "Alice" && notification.Trigger.Id == custom.Id);
            Assert.Contains(soundPaths, path => path.EndsWith("wall-break-soon.wav", StringComparison.Ordinal));
            Assert.Contains(soundPaths, path => path.EndsWith("vein-destroyed.wav", StringComparison.Ordinal));
            Assert.Equal(1, soundPaths.Count(path => path.EndsWith("vein-destroyed.wav", StringComparison.Ordinal)));
            Assert.Equal(1, popups.Count(notification =>
                notification.ProfileName == "Bob"
                && notification.Trigger.Id == EventAlertBuiltIns.WallBreakId));

            await pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await pipeline.DisposeAsync();
            try { await dispatcher.CompleteAsync(); }
            catch (AggregateException) { }
        }
    }

    private static async Task Step(ManualClock clock, CycleSignal cycles, long count)
    {
        await clock.WaitForTimer();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await cycles.WaitFor(count);
    }

    private sealed class EventFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WurmEventAlert-" + Guid.NewGuid().ToString("N"));

        public EventFiles()
        {
            PlayersPath = Path.Combine(_root, "players");
            Directory.CreateDirectory(PlayersPath);
            Resolver = new PlayerProfileIdentityResolver(Path.Combine(_root, "identity-registry"));
        }

        public string PlayersPath { get; }
        public PlayerProfileIdentityResolver Resolver { get; }

        public string Add(PlayerProfileId profileId, string profileName, string name, string text)
        {
            var directory = Path.Combine(PlayersPath, profileName, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, text);
            return path;
        }

        public IReadOnlyList<EventLogFileCandidate> Scan(string path) =>
            new EventLogReconciliationScanner().Scan(path);

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class SettingsFiles : IDisposable
    {
        public SettingsFiles()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "WurmEventAlertSettings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class TestSounds : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WurmAlertSounds-" + Guid.NewGuid().ToString("N"));

        public TestSounds()
        {
            var directory = Path.Combine(_root, "Sounds");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "wall-break-soon.wav"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "vein-destroyed.wav"), string.Empty);
        }

        public ExternalSoundService CreateService(Action<string> play) => new(_root, play);

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CycleSignal
    {
        private readonly object _sync = new();
        private TaskCompletionSource _changed = NewSignal();
        private long _count;

        public void Signal(long count)
        {
            lock (_sync)
            {
                _count = count;
                _changed.TrySetResult();
                _changed = NewSignal();
            }
        }

        public async Task WaitFor(long count)
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_count >= count)
                        return;
                    wait = _changed.Task;
                }

                await wait.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly SemaphoreSlim _registrations = new(0);
        private DateTimeOffset _now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public override long GetTimestamp() { lock (_sync) return _timestamp; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state, _timestamp + dueTime.Ticks);
                _timers.Add(timer);
                _registrations.Release();
                return timer;
            }
        }

        public async Task WaitForTimer() => await _registrations.WaitAsync(TimeSpan.FromSeconds(5));

        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_sync)
            {
                _timestamp += elapsed.Ticks;
                _now += elapsed;
                due = _timers.Where(timer => timer.DueTimestamp <= _timestamp).ToList();
                foreach (var timer in due)
                    timer.DueTimestamp = long.MaxValue;
            }

            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state, long dueTimestamp) : ITimer
        {
            public long DueTimestamp { get; set; } = dueTimestamp;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    DueTimestamp = owner._timestamp + dueTime.Ticks;
                    owner._registrations.Release();
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._sync)
                    owner._timers.Remove(this);
            }

            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
