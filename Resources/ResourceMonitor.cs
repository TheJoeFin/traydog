using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Win32;
using Launcher = Windows.System.Launcher;

sealed record ResourceState(bool Running, bool IoConnected, bool Connecting, ResourceOptions Options,
    IReadOnlyList<ResourceAlert> Alerts, DateTimeOffset? LastScan, int Candidates, int Unavailable,
    string? Error, string? StorageError, int Overflow);

sealed class ResourceMonitor : IMonitor
{
    public const string MonitorId = "resources";
    public static readonly ResourceMonitor Instance = new();
    public string Id => MonitorId;
    public string Name => "Process monitoring";
    public string Glyph => "\uE9D9";
    public void SetRunning(bool running) => SetMonitoring(running);

    static readonly ResourcePolicy Policy = new();
    static readonly Dictionary<ProcessKey, ProcessSample> Previous = [];
    static readonly HashSet<ProcessKey> ExitedDuringSample = [];
    static DispatcherQueue? _dispatcher;
    static DispatcherQueueTimer? _timer;
    static CancellationTokenSource? _run;
    static CancellationTokenSource? _connecting;
    static ResourceCollectorClient? _collector;
    static Task _collectorStopped = Task.CompletedTask;
    static bool _sampling;
    static int _generation;
    static double _nextScan;
    static long _eventsLost;
    static int _processorCount;

    public static ResourceState State { get; private set; } =
        new(false, false, false, new(), [], null, 0, 0, null, null, 0);

    public static event Action? Changed;
    public static event Action? AlertRaised;
    public static Action Subscribe(Action onChange)
    {
        Changed += onChange;
        return () => Changed -= onChange;
    }

    public static void Start()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _processorCount = Math.Max(1, (int)PInvoke.GetActiveProcessorCount(0xffff));
        _timer = _dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += async (_, _) => await SampleAsync();
        var options = ResourceStore.Load(out string? error);
        State = State with { Options = options, StorageError = error };
    }

    static void Publish(ResourceState next)
    {
        State = next;
        Changed?.Invoke();
    }

    static void SetMonitoring(bool on)
    {
        if (on == State.Running)
            return;
        _generation++;
        _timer?.Stop();
        _run?.Cancel();
        _run?.Dispose();
        _run = on ? new() : null;
        _nextScan = 0;
        Previous.Clear();
        Policy.ResetConfirmation();
        if (!on)
        {
            _connecting?.Cancel();
            StopCollector();
        }
        Publish(State with { Running = on, Candidates = 0, Error = null });
        if (on && !_sampling)
            Schedule(0.01);
    }

    public static void UpdateOptions(ResourceOptions options)
    {
        options = options.Sanitize();
        Policy.ResetConfirmation();
        _nextScan = 0;
        Publish(State with { Options = options, StorageError = ResourceStore.Save(options) });
        if (_collector is { } collector)
            _ = UpdateWatchAsync(collector, options.OnSightNames);
        if (State.Running && !_sampling)
            Schedule(0.01);
    }

    static async Task UpdateWatchAsync(ResourceCollectorClient collector, string[] names)
    {
        try { await collector.WatchAsync(names, CancellationToken.None); }
        catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException)
        {
            CollectorFailed(collector, ex.Message);
        }
    }

    public static string? AddRule(string input, bool onSight)
    {
        if (ProcessName.Normalize(input) is not { } name)
            return "Enter an executable name, such as example.exe, without a path or wildcards.";
        var options = State.Options;
        string[] list = onSight ? options.OnSightNames : options.IgnoredNames;
        if (list.Length >= 500 && !ProcessName.Contains(list, name))
            return "There can be at most 500 rules of each kind. Remove a rule before adding another.";
        UpdateOptions(onSight
            ? options with { OnSightNames = [.. options.OnSightNames, name], IgnoredNames = [.. options.IgnoredNames.Where(n => n != name)] }
            : options with { IgnoredNames = [.. options.IgnoredNames, name], OnSightNames = [.. options.OnSightNames.Where(n => n != name)] });
        if (!onSight)
            Publish(State with { Alerts = [.. State.Alerts.Where(a => !string.Equals(ProcessName.Normalize(a.Usage.Name), name, StringComparison.OrdinalIgnoreCase))] });
        return null;
    }

    public static void RemoveRule(string name, bool onSight) =>
        UpdateOptions(onSight
            ? State.Options with { OnSightNames = [.. State.Options.OnSightNames.Where(n => n != name)] }
            : State.Options with { IgnoredNames = [.. State.Options.IgnoredNames.Where(n => n != name)] });

    // Always a user gesture: startup and situations never cause an unattended UAC prompt.
    public static async Task EnableCollectorAsync()
    {
        if (State.Connecting || _collector is not null)
            return;
        if (!State.Running)
            Monitors.Choose(MonitorId, true);
        using var connect = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _connecting = connect;
        var collector = new ResourceCollectorClient();
        collector.Started += (key, name) => _dispatcher?.TryEnqueue(() =>
        {
            if (ReferenceEquals(_collector, collector) && State.Running && Policy.OnSight(key, name, State.Options))
                AddAlert(new(new(key, name, null, 0, null, null), ResourceKind.OnSight, DateTimeOffset.Now));
        });
        collector.Failed += error => _dispatcher?.TryEnqueue(() => CollectorFailed(collector, error));
        collector.Exited += key => _dispatcher?.TryEnqueue(() =>
        {
            if (ReferenceEquals(_collector, collector))
            {
                if (_sampling)
                    ExitedDuringSample.Add(key);
                else
                {
                    Policy.Forget(key);
                    Previous.Remove(key);
                }
            }
        });
        Publish(State with { Connecting = true, Error = null });
        try
        {
            await _collectorStopped;
            await collector.ConnectAsync(State.Options.OnSightNames, connect.Token);
            connect.Token.ThrowIfCancellationRequested();
            _collector = collector;
            _eventsLost = 0;
            Previous.Clear();
            Policy.ResetConfirmation();
            _nextScan = 0;
            Publish(State with { IoConnected = true });
            if (!_sampling)
                Schedule(0.01);
        }
        catch (Exception ex) when (ex is Win32Exception or System.IO.IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
        {
            CrashLog.Write(ex);
            await CloseCollectorAsync(collector);
            Publish(State with { Error = ex is Win32Exception { NativeErrorCode: 1223 }
                ? "Permission was canceled. CPU/RAM and scan-based alert-on-sight still work."
                : "Disk/Network collector could not start. CPU/RAM still work. Try enabling it again." });
        }
        finally
        {
            if (ReferenceEquals(_connecting, connect))
                _connecting = null;
            Publish(State with { Connecting = false });
        }
    }

    public static void DisableCollector()
    {
        _connecting?.Cancel();
        StopCollector();
        Publish(State with { IoConnected = false });
    }

    static void StopCollector()
    {
        if (_collector is not { } collector)
            return;
        _collector = null;
        _collectorStopped = CloseCollectorAsync(collector);
        Previous.Clear();
        Policy.ResetConfirmation();
        State = State with { IoConnected = false };
    }

    static async Task CloseCollectorAsync(ResourceCollectorClient collector)
    {
        try { await collector.DisposeAsync(); }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or System.IO.IOException)
        {
            CrashLog.Write(ex);
            Publish(State with { Error = "The collector did not shut down cleanly. Resource monitoring is disconnected; check traydog-crash.log." });
        }
    }

    static void CollectorFailed(ResourceCollectorClient collector, string error)
    {
        if (!ReferenceEquals(_collector, collector))
            return;
        StopCollector();
        Publish(State with { Error = $"Disk/Network collection stopped. {error}" });
    }

    static async Task SampleAsync()
    {
        if (!State.Running || _sampling || _run is null)
            return;
        _sampling = true;
        int generation = _generation;
        var token = _run.Token;
        bool full = ProcessSampler.Now >= _nextScan;
        var targets = full ? null : Policy.Candidates;
        var collector = _collector;
        try
        {
            var scan = await Task.Run(() => ProcessSampler.Read(targets), token);
            CollectorMessage? io = null;
            if (collector is not null)
            {
                try
                {
                    io = await collector.SampleAsync(targets?.Select(k => k.Pid).ToArray(),
                        full ? scan.AlivePids.ToArray() : null, token);
                }
                catch (Exception ex) when (ex is System.IO.IOException or TimeoutException or ObjectDisposedException)
                {
                    CollectorFailed(collector, ex.Message);
                }
            }
            if (token.IsCancellationRequested || generation != _generation)
                return;
            bool completeIo = io is not null && io.EventsLost == _eventsLost && ReferenceEquals(collector, _collector);
            if (io is not null && io.EventsLost != _eventsLost)
            {
                _eventsLost = io.EventsLost;
                foreach (var key in Previous.Keys.ToArray())
                    Previous[key] = Previous[key] with { DiskBytes = null, NetworkBytes = null };
                Publish(State with { Error = "Windows dropped resource events. Disk/Network confirmation was reset; short process launches may have been missed." });
            }
            var counters = io?.Samples?.ToDictionary(s => s.Key.Pid);
            var alive = scan.Samples.Select(s => s.Key).ToHashSet();
            var options = State.Options;
            double now = ProcessSampler.Now;
            foreach (var raw in scan.Samples)
            {
                if (ExitedDuringSample.Contains(raw.Key))
                    continue;
                IoSample? bytes = counters?.GetValueOrDefault(raw.Key.Pid);
                bool known = completeIo && (bytes is null || bytes.Key == raw.Key);
                var sample = raw with
                {
                    DiskBytes = known ? bytes?.DiskBytes ?? 0 : null,
                    NetworkBytes = known ? bytes?.NetworkBytes ?? 0 : null,
                };
                var usage = ResourceUsage.From(sample, Previous.GetValueOrDefault(sample.Key), _processorCount);
                Previous[sample.Key] = sample;
                ResourceKind kinds = Policy.Observe(usage, options, now);
                if (full && Policy.OnSight(sample.Key, sample.Name, options))
                    kinds |= ResourceKind.OnSight;
                if (kinds != ResourceKind.None)
                    AddAlert(new(usage, kinds, DateTimeOffset.Now, options.SustainedSeconds));
            }
            if (full)
            {
                // A temporarily unreadable process is not an exited process. Keep its mute/cooldown,
                // but discard its baseline so a gap cannot masquerade as confirmed high usage.
                foreach (var key in Previous.Keys.Where(k => !alive.Contains(k)).ToArray())
                {
                    Previous.Remove(key);
                    Policy.ResetConfirmation(key);
                }
                Policy.ForgetMissing(alive, scan.AlivePids.Except(alive.Select(k => k.Pid)).ToHashSet());
                _nextScan = now + options.ScanSeconds;
            }
            else if (targets is not null)
            {
                foreach (var key in targets.Where(k => !alive.Contains(k)))
                {
                    Previous.Remove(key);
                    if (scan.AlivePids.Contains(key.Pid))
                        Policy.ResetConfirmation(key);
                    else
                        Policy.Forget(key);
                }
            }
            Publish(State with
            {
                LastScan = full ? DateTimeOffset.Now : State.LastScan,
                Candidates = Policy.Candidates.Count,
                Unavailable = full ? scan.UnavailableCount : State.Unavailable,
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            CrashLog.Write(ex);
            Policy.ResetConfirmation();
            _nextScan = ProcessSampler.Now + State.Options.ScanSeconds;
            Publish(State with { Error = "Processes could not be sampled. Monitoring will retry at the next scan.", Candidates = 0 });
        }
        finally
        {
            foreach (var key in ExitedDuringSample)
            {
                Policy.Forget(key);
                Previous.Remove(key);
            }
            ExitedDuringSample.Clear();
            _sampling = false;
            if (State.Running)
            {
                double delay = Math.Max(0.01, _nextScan - ProcessSampler.Now);
                Schedule(Policy.Candidates.Count > 0 ? Math.Min(delay, State.Options.ConfirmSeconds) : delay);
            }
        }
    }

    static void Schedule(double seconds)
    {
        if (_timer is null)
            return;
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    static void AddAlert(ResourceAlert alert)
    {
        bool isNew = State.Alerts.All(a => a.Usage.Key != alert.Usage.Key);
        var alerts = ResourceAlertList.Add(State.Alerts, alert, out bool overflow);
        Publish(State with
        {
            Alerts = alerts,
            Overflow = State.Overflow + (overflow ? 1 : 0)
        });
        if (isNew && !overflow)
            AlertRaised?.Invoke();
    }

    public static void Dismiss(ResourceAlert alert) => ApplyAlerts([alert], ResourceAlertAction.Dismiss);

    public static void IgnoreOnce(ResourceAlert alert) => ApplyAlerts([alert], ResourceAlertAction.IgnoreOnce);

    public static void IgnoreAlways(ResourceAlert alert) => ApplyAlerts([alert], ResourceAlertAction.IgnoreAlways);

    public static void ApplyAlerts(IReadOnlyList<ResourceAlert> alerts, ResourceAlertAction action)
    {
        var targets = ResourceAlertList.Selected(State.Alerts, alerts.Select(a => a.Usage.Key));
        if (targets.Length == 0)
            return;

        var keys = targets.Select(a => a.Usage.Key).ToHashSet();
        if (action == ResourceAlertAction.IgnoreAlways)
        {
            if (ResourceAlertList.IgnoreOptions(State.Options, targets, out string? error) is not { } options)
            {
                Publish(State with { Error = error });
                return;
            }
            UpdateOptions(options);
        }
        else
        {
            double now = ProcessSampler.Now;
            foreach (var alert in targets)
            {
                if (action == ResourceAlertAction.Dismiss)
                    Policy.Dismiss(alert.Usage.Key, now);
                else
                    Policy.IgnoreOnce(alert.Usage.Key);
            }
        }
        Publish(State with
        {
            Alerts = [.. State.Alerts.Where(a => action == ResourceAlertAction.IgnoreAlways
                ? !ProcessName.Contains(State.Options.IgnoredNames, a.Usage.Name) : !keys.Contains(a.Usage.Key))]
        });
    }

    public static async Task SearchAsync(ResourceAlert alert)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(ResourceText.SearchUri(alert.Usage.Name)))
                Publish(State with { Error = "Windows could not open the browser. Check your default browser settings." });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            CrashLog.Write(ex);
            Publish(State with { Error = "The online search could not be opened." });
        }
    }
}
