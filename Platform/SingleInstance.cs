using System;
using System.Threading;
using Windows.Win32;

// One traydog per user session. A named mutex marks the running instance; a named event lets a
// second launch ask it to open its popout, after which the second launch exits.
sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\traydog_SingleInstance";
    const string ShowEventName = @"Local\traydog_ShowPopout";
    const uint ASFW_ANY = unchecked((uint)-1);

    readonly Mutex _mutex;
    readonly EventWaitHandle _showEvent;
    RegisteredWaitHandle? _showWait;

    SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
    }

    // Returns the instance guard, or null when another traydog is already running. The mutex is
    // only an existence marker (never owned), so it doesn't care which thread disposes it.
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        if (createdNew)
            return new SingleInstance(mutex);

        mutex.Dispose();
        return null;
    }

    // Asks the running instance to show its popout. Called by the second launch before it exits.
    public static void SignalRunningInstance()
    {
        try
        {
            // Let the running instance take the foreground when it shows its popout.
            PInvoke.AllowSetForegroundWindow(ASFW_ANY);
            using var existing = EventWaitHandle.OpenExisting(ShowEventName);
            existing.Set();
        }
        catch
        {
            // The other instance may be shutting down; nothing to do.
        }
    }

    // Invokes onShowRequested (on a thread-pool thread) each time another launch signals.
    public void ListenForShowRequests(Action onShowRequested) =>
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _showWait?.Unregister(null);
        _showEvent.Dispose();
        _mutex.Dispose();
    }
}
