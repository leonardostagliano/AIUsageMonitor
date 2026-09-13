namespace AIUsageMonitor.App.Startup;

/// <summary>Named mutex for single instance; a second launch signals the first one to pin the notch open.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\AIUsageMonitor.Instance";
    private const string EventName = @"Local\AIUsageMonitor.ShowNotch";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _registration;

    public event Action? ShowNotchRequested;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => ShowNotchRequested?.Invoke(), null, -1, executeOnlyOnce: false);
    }

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public static void SignalShowNotch()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(EventName);
            evt.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _showEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
