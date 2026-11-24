using System.Security.Principal;

namespace Tunnelman;

internal sealed class SingleInstance : IDisposable
{
    public const string MutexNamePrefix = "Tunnelman.SingleInstance";
    public const string EventNamePrefix = "Tunnelman.ShowWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _waitTask;
    private bool _disposed;

    public event Action? ShowRequested;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _waitTask = Task.Run(WaitLoop);
    }

    public static bool TryStart(string profileSuffix, out SingleInstance? instance)
    {
        instance = null;
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
            return TryAcquire("Local", Environment.MachineName, profileSuffix, out instance, out _);

        if (TryAcquire("Global", sid, profileSuffix, out instance, out var fallback) && instance is not null)
            return true;
        if (!fallback)
            return false;
        return TryAcquire("Local", sid, profileSuffix, out instance, out _);
    }

    private static bool TryAcquire(string scope, string suffix, string profileSuffix, out SingleInstance? instance, out bool canFallback)
    {
        instance = null;
        canFallback = false;
        var mutexName = $@"{scope}\{MutexNamePrefix}.{suffix}{profileSuffix}";
        var eventName = $@"{scope}\{EventNamePrefix}.{suffix}{profileSuffix}";
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out var created);
            if (!created)
            {
                mutex.Dispose();
                mutex = null;
                TrySignal(eventName);
                return false;
            }

            EventWaitHandle showEvent;
            try
            {
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            }
            catch
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }

                mutex.Dispose();
                mutex = null;
                throw;
            }

            instance = new SingleInstance(mutex, showEvent);
            mutex = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            TrySignal(eventName);
            return false;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or ArgumentException)
        {
            canFallback = true;
            return false;
        }
        finally
        {
            if (mutex is not null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }

                mutex.Dispose();
            }
        }
    }

    private static void TrySignal(string eventName)
    {
        try
        {
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            ev.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private void WaitLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_showEvent.WaitOne(500))
                    ShowRequested?.Invoke();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        try
        {
            _waitTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _showEvent.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
        _cts.Dispose();
    }
}
