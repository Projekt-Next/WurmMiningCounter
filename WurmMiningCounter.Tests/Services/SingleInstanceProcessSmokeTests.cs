using System.Diagnostics;
using System.IO.Pipes;

namespace WurmMiningCounter.Tests.Services;

public sealed class SingleInstanceProcessSmokeTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SecondaryProcessExitsWhilePrimaryProcessRemainsRunning()
    {
        using var fixture = new ProcessSmokeFixture();
        Process? primary = null;
        Process? secondary = null;

        try
        {
            primary = fixture.StartApplication();
            await WaitForPipeListenerAsync(fixture.PipeName);
            Assert.True(IsRunning(primary));

            secondary = fixture.StartApplication();
            await secondary.WaitForExitAsync().WaitAsync(ProcessTimeout);

            Assert.True(secondary.HasExited);
            Assert.True(IsRunning(primary));
            Assert.Equal(1, new[] { primary, secondary }.Count(IsRunning));
        }
        finally
        {
            await StopProcessAsync(secondary);
            await StopProcessAsync(primary);
        }
    }

    private static async Task WaitForPipeListenerAsync(string pipeName)
    {
        var deadline = DateTime.UtcNow + ProcessTimeout;

        while (DateTime.UtcNow < deadline)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            try
            {
                await client.ConnectAsync(cancellation.Token);
                return;
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException("Primary application process did not start its activation pipe in time.");
    }

    private static bool IsRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (IsRunning(process))
                process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
        }
        catch (InvalidOperationException)
        {
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed class ProcessSmokeFixture : IDisposable
    {
        private readonly string _temporaryDirectoryPath;

        public ProcessSmokeFixture()
        {
            var suffix = Guid.NewGuid().ToString("N");
            _temporaryDirectoryPath = Path.Combine(Path.GetTempPath(), "WurmMiningCounter.Tests", suffix);
            SettingsDirectoryPath = Path.Combine(_temporaryDirectoryPath, "settings");
            MutexName = $"Local\\WurmMiningCounter.ProcessSmoke.{suffix}";
            PipeName = $"WurmMiningCounter.ProcessSmoke.{suffix}";
        }

        public string MutexName { get; }

        public string PipeName { get; }

        public string SettingsDirectoryPath { get; }

        public Process StartApplication()
        {
            var executablePath = Path.Combine(AppContext.BaseDirectory, "WurmMiningCounter.exe");
            Assert.True(File.Exists(executablePath), $"Application executable was not found: {executablePath}");

            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.Environment["WURM_MINING_COUNTER_MUTEX_NAME"] = MutexName;
            startInfo.Environment["WURM_MINING_COUNTER_PIPE_NAME"] = PipeName;
            startInfo.Environment["WURM_MINING_COUNTER_SETTINGS_DIRECTORY"] = SettingsDirectoryPath;

            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Application process could not be started.");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_temporaryDirectoryPath))
                    Directory.Delete(_temporaryDirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
