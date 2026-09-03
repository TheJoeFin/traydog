using System;
using Microsoft.UI.Dispatching;          // DispatcherQueue

// Connectivity hooks for the App component: UseConnectivity owns a ConnectivityMonitor while
// monitoring is on and marshals its background events onto the UI thread; UseRefreshProgress
// drives the countdown bar between probes.
partial class App
{
    // Custom hook: runs a ConnectivityMonitor while monitoring is on, marshalling its
    // background status and scheduling updates back onto the UI thread.
    void UseConnectivity(
        bool monitoring,
        Action<NetState> setNet,
        Action<DateTimeOffset?> setNextRefreshAt)
    {
        UseEffect(() =>
        {
            if (!monitoring)
            {
                setNet(new NetState(NetStatus.Unknown, "Not monitoring"));
                setNextRefreshAt(null);
                return () => { };
            }

            var dispatcher = DispatcherQueue.GetForCurrentThread();
            setNet(new NetState(NetStatus.Unknown, "Checking…"));
            setNextRefreshAt(null);

            var monitor = new ConnectivityMonitor();
            monitor.StatusChanged += (status, detail) =>
                dispatcher?.TryEnqueue(() => setNet(new NetState(status, detail)));
            monitor.NextRefreshScheduled += nextRefreshAt =>
                dispatcher?.TryEnqueue(() => setNextRefreshAt(nextRefreshAt));
            monitor.Start();

            return () => monitor.Dispose();
        }, monitoring);
    }

    void UseRefreshProgress(
        bool monitoring,
        bool popoutVisible,
        DateTimeOffset? nextRefreshAt,
        Action<double> setProgress)
    {
        UseEffect(() =>
        {
            if (!monitoring || !popoutVisible || nextRefreshAt is null)
            {
                setProgress(0);
                return () => { };
            }

            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var timer = dispatcher?.CreateTimer();
            if (timer is null)
                return () => { };

            void UpdateProgress()
            {
                var secondsRemaining = Math.Max(
                    0,
                    (nextRefreshAt.Value - DateTimeOffset.UtcNow).TotalSeconds);
                var progress = Math.Clamp(
                    (1 - secondsRemaining / ConnectivityMonitor.RefreshInterval.TotalSeconds) * 100,
                    0,
                    100);

                setProgress(progress);
            }

            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (_, _) => UpdateProgress();
            UpdateProgress();
            timer.Start();

            return () => timer.Stop();
        }, monitoring, popoutVisible, nextRefreshAt.GetValueOrDefault());
    }
}
