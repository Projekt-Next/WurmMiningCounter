using System.Collections.Concurrent;
using System.Diagnostics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventNewSegmentOwnershipTests
{
    [Fact]
    public async Task ChangedDirectoriesStaySuspendedUntilFreshStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-owner-restart-" + Guid.NewGuid().ToString("N"));
        var players = Path.Combine(root, "players");
        var alice = Path.Combine(players, "Alice");
        var bob = Path.Combine(players, "Bob");
        var carol = Path.Combine(players, "Carol");
        var logsA = Path.Combine(root, "LogsA");
        var logsB = Path.Combine(root, "LogsB");
        var logsC = Path.Combine(root, "LogsC");
        var aliceLink = Path.Combine(alice, "logs");
        var bobLink = Path.Combine(bob, "logs");
        void Link(string path, string target)
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            { ArgumentList = { "/c", "mklink", "/J", path, target }, UseShellExecute = false, CreateNoWindow = true })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            foreach (var path in new[] { alice, bob, logsA, logsB, logsC, Path.Combine(carol, "logs") }) Directory.CreateDirectory(path);
            Link(aliceLink, logsB);
            Link(bobLink, logsA);
            var now = DateTimeOffset.Now;
            var shared = Path.Combine(logsB, $"_Event.{now:yyyy-MM}.txt");
            var late = Path.Combine(logsB, $"_Event.{now:yyyy-MM-dd}.txt");
            var independent = Path.Combine(carol, "logs", $"_Event.{now:yyyy-MM}.txt");
            File.WriteAllText(shared, "history\n");
            File.WriteAllText(independent, "history\n");
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var locator = new WurmLogLocationLocator(Directory.Exists, Directory.EnumerateDirectories,
                WurmDiscoveryPathPolicy.CanonicalizeDirectory, new WindowsDirectoryPhysicalIdentityProvider(), () => players, _ => [],
                _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []), Directory.EnumerateFiles,
                path => Directory.Exists(path) ? WurmLogDirectoryProbeResult.Exists : WurmLogDirectoryProbeResult.Missing);
            var discovery = new LogDiscoverySnapshotService(locator, resolver);
            var location = new KnownLogLocation { Id = players, Enabled = true, Game = "Test", Type = KnownLogLocations.DirectType, Path = players };
            var initial = discovery.Discover([location], []);
            var clock = new ControlledEventRetirementTests.ManualClock(now);
            var output = new ConcurrentQueue<EventPipelineOutput>();
            await using var pipeline = new EventPipeline(players, item => { output.Enqueue(item); return EventPipelineHandoffResult.Complete; },
                new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: initial.SourceCandidates);
            void Publish(LogDiscoverySnapshot snapshot, bool authoritative)
            {
                var publication = discovery.GetCurrentSnapshot()!;
                pipeline.PublishDiscoverySnapshot(publication.Version, snapshot.SourceCandidates, snapshot.Discovery.SourceOwnershipAmbiguities, authoritative);
                pipeline.AddSourceCandidates(publication.Version, snapshot.SourceCandidates);
            }
            pipeline.Start();
            var checkpoint = Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == shared);
            Directory.Delete(bobLink);
            Link(bobLink, logsB);
            var conflict = discovery.Discover([location], []);
            Assert.NotEmpty(conflict.Discovery.SourceOwnershipAmbiguities);
            Publish(conflict, false);
            pipeline.RunCycleForTest();
            Directory.Delete(aliceLink);
            Link(aliceLink, logsC);
            var resolved = discovery.Discover([location], []);
            Publish(resolved, true);
            pipeline.RunCycleForTest();
            File.WriteAllText(late, "during suspension\n");
            File.AppendAllText(shared, "not attributed\n");
            File.AppendAllText(independent, "carol\n");
            clock.Advance(TimeSpan.FromSeconds(10));
            pipeline.RunCycleForTest();
            pipeline.DiscoverForTest();
            pipeline.RunCycleForTest();
            Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == shared));
            Assert.DoesNotContain(pipeline.SourceCheckpoints, item => item.CanonicalPath == late);
            Assert.DoesNotContain(output.OfType<EventReadBatch>(), item => item.CanonicalPath == shared || item.CanonicalPath == late);
            Assert.Equal(["carol"], Assert.Single(output.OfType<EventReadBatch>(), item => item.CanonicalPath == independent).Lines);
            await pipeline.StopAsync();

            var restartedOutput = new ConcurrentQueue<EventPipelineOutput>();
            await using var restarted = new EventPipeline(players, item => { restartedOutput.Enqueue(item); return EventPipelineHandoffResult.Complete; },
                new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: resolved.SourceCandidates);
            restarted.Start();
            var baseline = Assert.Single(restarted.SourceCheckpoints, item => item.CanonicalPath == late);
            Assert.Equal("Bob", baseline.ProfileName);
            Assert.Equal(new FileInfo(late).Length, baseline.ReadOffset);
            Assert.Empty(restartedOutput);
            File.AppendAllText(late, "after restart\n");
            restarted.RunCycleForTest();
            Assert.Equal(["after restart"], Assert.Single(restartedOutput.OfType<EventReadBatch>()).Lines);
            await restarted.StopAsync();
        }
        finally
        {
            foreach (var path in new[] { aliceLink, bobLink }) if (Directory.Exists(path)) Directory.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 2)]
    [InlineData(true, true, 3)]
    public async Task NewSegmentWaitsForAuthoritativeOwner(bool transferToBob, bool createdAfterResolution, int laterUncertainty)
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-segments-" + Guid.NewGuid().ToString("N"));
        var players = Path.Combine(root, "players");
        var alice = Path.Combine(players, "Alice");
        var bob = Path.Combine(players, "Bob");
        var carol = Path.Combine(players, "Carol");
        var logs = Path.Combine(alice, "logs");
        var bobLink = Path.Combine(bob, "logs");
        try
        {
            Directory.CreateDirectory(logs);
            Directory.CreateDirectory(bob);
            Directory.CreateDirectory(Path.Combine(carol, "logs"));
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", bobLink, logs }, UseShellExecute = false, CreateNoWindow = true
            })!)
            {
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }
            var now = DateTimeOffset.Now;
            var shared = Path.Combine(logs, $"_Event.{now:yyyy-MM}.txt");
            var fresh = Path.Combine(logs, $"_Event.{now:yyyy-MM-dd}.txt");
            var other = Path.Combine(carol, "logs", $"_Event.{now:yyyy-MM}.txt");
            File.WriteAllText(shared, "history\n");
            File.WriteAllText(other, "history\n");
            var mode = 0;
            var locator = new WurmLogLocationLocator(Directory.Exists,
                path => path == players ? mode switch { 1 => [alice, bob, carol], 2 => [bob, carol], _ => [alice, carol] } : Directory.EnumerateDirectories(path),
                WurmDiscoveryPathPolicy.CanonicalizeDirectory, new WindowsDirectoryPhysicalIdentityProvider(), () => players, _ => [],
                _ => new SteamInstallationDiscoveryResult(new Dictionary<int, IReadOnlyList<string>>(), []), Directory.EnumerateFiles,
                path => Directory.Exists(path) ? WurmLogDirectoryProbeResult.Exists : WurmLogDirectoryProbeResult.Missing);
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var discovery = new LogDiscoverySnapshotService(locator, resolver);
            var location = new KnownLogLocation { Id = players, Enabled = true, Game = "Test", Type = KnownLogLocations.DirectType, Path = players };
            var first = discovery.Discover([location], []);
            var firstPublication = discovery.GetCurrentSnapshot()!;
            var outputs = new ConcurrentQueue<EventPipelineOutput>();
            var clock = new ControlledEventRetirementTests.ManualClock(now);
            await using var pipeline = new EventPipeline(players, output => { outputs.Enqueue(output); return EventPipelineHandoffResult.Complete; },
                new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: first.SourceCandidates);
            void Publish(LogDiscoverySnapshot snapshot, bool authoritative)
            {
                var publication = discovery.GetCurrentSnapshot()!;
                pipeline.PublishDiscoverySnapshot(publication.Version, snapshot.SourceCandidates, snapshot.Discovery.SourceOwnershipAmbiguities, authoritative);
                pipeline.AddSourceCandidates(publication.Version, snapshot.SourceCandidates);
            }
            pipeline.Start();
            var aliceId = Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == shared).ProfileId;
            mode = 1;
            var conflict = discovery.Discover([location], []);
            Assert.NotEmpty(conflict.Discovery.SourceOwnershipAmbiguities);
            Publish(conflict, false);
            pipeline.RunCycleForTest();
            if (!createdAfterResolution) File.WriteAllText(fresh, "during conflict\n");
            File.AppendAllText(other, "carol continues\n");
            pipeline.DiscoverForTest();
            pipeline.RunCycleForTest();
            Assert.DoesNotContain(pipeline.SourceCheckpoints, item => item.CanonicalPath == fresh);
            Assert.DoesNotContain(outputs.OfType<EventReadBatch>(), item => item.CanonicalPath == fresh);
            Assert.Equal(["carol continues"], Assert.Single(outputs.OfType<EventReadBatch>(), item => item.CanonicalPath == other).Lines);
            mode = transferToBob ? 2 : 0;
            var resolved = discovery.Discover([location], []);
            Assert.True(resolved.IsComplete);
            Assert.Empty(resolved.Discovery.SourceOwnershipAmbiguities);
            Publish(resolved, false);
            pipeline.PublishDiscoverySnapshot(firstPublication.Version, first.SourceCandidates, first.Discovery.SourceOwnershipAmbiguities, true);
            pipeline.RunCycleForTest();
            pipeline.DiscoverForTest();
            pipeline.RunCycleForTest();
            Assert.DoesNotContain(pipeline.SourceCheckpoints, item => item.CanonicalPath == fresh);
            resolved = discovery.Discover([location], []);
            var hiddenBob = Path.Combine(players, "Bob-hidden");
            if (transferToBob && !createdAfterResolution) Directory.Move(bob, hiddenBob);
            Publish(resolved, true);
            pipeline.RunCycleForTest();
            if (laterUncertainty is 1 or 2)
            {
                if (laterUncertainty == 2) mode = 1;
                var uncertain = discovery.Discover([location], []);
                Publish(uncertain, false);
                pipeline.RunCycleForTest();
            }
            if (laterUncertainty == 3)
            {
                var otherLogs = Path.Combine(root, "other-logs");
                Directory.CreateDirectory(otherLogs);
                Directory.Delete(bobLink);
                using var process = Process.Start(new ProcessStartInfo("cmd.exe")
                {
                    ArgumentList = { "/c", "mklink", "/J", bobLink, otherLogs }, UseShellExecute = false, CreateNoWindow = true
                })!;
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }
            if (createdAfterResolution) File.WriteAllText(fresh, "late segment\n");
            if (laterUncertainty != 0)
            {
                pipeline.DiscoverForTest();
                pipeline.RunCycleForTest();
                Assert.DoesNotContain(pipeline.SourceCheckpoints, item => item.CanonicalPath == fresh);
                File.AppendAllText(other, "carol after uncertainty\n");
                pipeline.RunCycleForTest();
                Assert.Contains(outputs.OfType<EventReadBatch>(), item => item.CanonicalPath == other && item.Lines.Contains("carol after uncertainty"));
                if (laterUncertainty == 3)
                {
                    Directory.Delete(bobLink);
                    using var process = Process.Start(new ProcessStartInfo("cmd.exe")
                    {
                        ArgumentList = { "/c", "mklink", "/J", bobLink, logs }, UseShellExecute = false, CreateNoWindow = true
                    })!;
                    process.WaitForExit();
                    Assert.Equal(0, process.ExitCode);
                }
                else
                {
                    mode = transferToBob ? 2 : 0;
                    resolved = discovery.Discover([location], []);
                    Publish(resolved, true);
                    pipeline.PublishDiscoverySnapshot(firstPublication.Version, first.SourceCandidates, first.Discovery.SourceOwnershipAmbiguities, true);
                    pipeline.RunCycleForTest();
                }
            }
            pipeline.DiscoverForTest();
            pipeline.RunCycleForTest();
            if (transferToBob)
            {
                if (!createdAfterResolution) Directory.Move(hiddenBob, bob);
                clock.Advance(TimeSpan.FromSeconds(10));
                pipeline.RunCycleForTest();
                pipeline.DiscoverForTest();
                pipeline.RunCycleForTest();
                Assert.DoesNotContain(pipeline.SourceCheckpoints, item => item.CanonicalPath == fresh);
                Assert.Equal(aliceId, Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == shared).ProfileId);
                Assert.DoesNotContain(outputs.OfType<EventReadBatch>(), item => item.CanonicalPath == shared || item.CanonicalPath == fresh);
                await pipeline.StopAsync();
                return;
            }
            var segment = Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == fresh);
            Assert.Equal("Alice", segment.ProfileName);
            Assert.Equal(aliceId, segment.ProfileId);
            await pipeline.StopAsync();
        }
        finally
        {
            if (Directory.Exists(bobLink)) Directory.Delete(bobLink);
            var hiddenLink = Path.Combine(players, "Bob-hidden", "logs");
            if (Directory.Exists(hiddenLink)) Directory.Delete(hiddenLink);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
