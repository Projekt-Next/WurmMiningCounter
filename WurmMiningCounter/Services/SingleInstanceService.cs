using System.IO.Pipes;
using System.IO;
using System.Text;

namespace WurmMiningCounter.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string DefaultMutexName = "Local\\WurmMiningCounter";
    private const string DefaultPipeName = "WurmMiningCounter.Activate";
    private const string MutexNameEnvironmentVariable = "WURM_MINING_COUNTER_MUTEX_NAME";
    private const string PipeNameEnvironmentVariable = "WURM_MINING_COUNTER_PIPE_NAME";
    private const string ActivateMessage = "ACTIVATE";
    private const int ConnectionAttempts = 5;
    private const int ConnectionTimeoutMilliseconds = 50;
    private const int ConnectionRetryDelayMilliseconds = 25;

    private readonly object _lifecycleSync = new();
    private readonly string _pipeName;
    private readonly Action _activate;
    private readonly TimeSpan _listenerStartupDelay;

    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellation;
    private int _isPrimary;
    private int _disposeRequested;

    public SingleInstanceService(Action activate)
        : this(
            GetConfiguredName(MutexNameEnvironmentVariable, DefaultMutexName),
            GetConfiguredName(PipeNameEnvironmentVariable, DefaultPipeName),
            activate)
    {
    }

    internal SingleInstanceService(
        string mutexName,
        string pipeName,
        Action activate,
        TimeSpan? listenerStartupDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(activate);

        _pipeName = pipeName;
        _activate = activate;
        _listenerStartupDelay = listenerStartupDelay ?? TimeSpan.Zero;

        var mutex = new Mutex(false, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return;
        }

        _mutex = mutex;
        Volatile.Write(ref _isPrimary, 1);
        var listenerCancellation = new CancellationTokenSource();
        _listenerCancellation = listenerCancellation;
        _ = Task.Run(() => RunListenerAsync(listenerCancellation));
    }

    public bool IsPrimary => Volatile.Read(ref _isPrimary) != 0;

    public bool SendActivate() => SendMessage(ActivateMessage);

    internal bool SendMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Volatile.Read(ref _disposeRequested) != 0 || IsPrimary)
            return false;

        for (var attempt = 0; attempt < ConnectionAttempts; attempt++)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
                return false;

            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.None);
                client.Connect(ConnectionTimeoutMilliseconds);

                using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: false)
                {
                    AutoFlush = true
                };
                writer.WriteLine(message);
                return true;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            if (attempt < ConnectionAttempts - 1)
                Thread.Sleep(ConnectionRetryDelayMilliseconds);
        }

        return false;
    }

    public void Dispose()
    {
        CancellationTokenSource? listenerCancellation;
        Mutex? mutex;
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
                return;

            Volatile.Write(ref _disposeRequested, 1);
            listenerCancellation = _listenerCancellation;
            _listenerCancellation = null;
            mutex = _mutex;
            _mutex = null;
            Volatile.Write(ref _isPrimary, 0);
        }

        try
        {
            listenerCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        mutex?.Dispose();
    }

    private async Task RunListenerAsync(CancellationTokenSource listenerCancellation)
    {
        var cancellationToken = listenerCancellation.Token;
        try
        {
            if (_listenerStartupDelay > TimeSpan.Zero)
                await Task.Delay(_listenerStartupDelay, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: false);
                    var message = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (message == ActivateMessage)
                        InvokeActivateIfAllowed();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        await Task.Delay(ConnectionRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            listenerCancellation.Dispose();
        }
    }

    private void InvokeActivateIfAllowed()
    {
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
                return;

            try
            {
                _activate();
            }
            catch
            {
            }
        }
    }

    private static string GetConfiguredName(string environmentVariable, string defaultValue)
    {
        var configuredValue = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(configuredValue) ? defaultValue : configuredValue;
    }
}
