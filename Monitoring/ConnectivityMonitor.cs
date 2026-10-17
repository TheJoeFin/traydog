using System;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

// Probes connectivity on an interval and on OS network-change events, modelled on
// SurfaceIssueTracking's ConnectivityMonitor (ping 8.8.8.8 + an HTTP connect test). One probe runs
// at a time, Interval can change while running, and Dispose cancels an in-flight probe.
// Events are raised on a background thread — marshal to the UI thread before touching UI/state.
sealed class ConnectivityMonitor : IDisposable
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    static readonly IPAddress PingTarget = IPAddress.Parse("8.8.8.8");
    static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);

    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _wake = new(0, 1); // released to make the poll loop re-plan its wait
    TimeSpan _interval;
    DateTimeOffset _lastProbeAt = DateTimeOffset.MinValue;
    volatile bool _probeRequested;

    public ConnectivityMonitor(TimeSpan interval) => _interval = interval;

    public event Action<NetStatus, string>? StatusChanged;

    // (last probe, next probe), raised whenever the loop starts waiting.
    public event Action<DateTimeOffset, DateTimeOffset>? Scheduled;

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = value;
            Wake();
        }
    }

    public void Start()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _ = PollLoopAsync(_cts.Token);
    }

    // Probe now, or as soon as the probe in flight finishes.
    public void ProbeNow()
    {
        _probeRequested = true;
        Wake();
    }

    // A network-change event almost always means the answer just changed — re-probe now
    // instead of waiting for the next tick.
    void OnNetworkChanged(object? sender, EventArgs e) => ProbeNow();

    void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { } // already pending
    }

    async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TimeSpan wait = _probeRequested
                    ? TimeSpan.Zero
                    : _lastProbeAt + _interval - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    Scheduled?.Invoke(_lastProbeAt, DateTimeOffset.UtcNow + wait);
                    await _wake.WaitAsync(wait, ct); // woken early by Interval changes and ProbeNow
                    continue;
                }

                _probeRequested = false;
                await CheckAsync(ct);
                _lastProbeAt = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed.
        }
    }

    async Task CheckAsync(CancellationToken ct)
    {
        bool networkUp = NetworkInterface.GetIsNetworkAvailable();
        var (pingOk, pingMsg) = await PingAsync(ct);
        var (httpOk, httpMsg) = await HttpCheckAsync(ct);
        ct.ThrowIfCancellationRequested();

        StatusChanged?.Invoke(Classify(httpOk, pingOk, networkUp), $"ping {pingMsg} · http {httpMsg}");
    }

    // A real HTTP round-trip is the only proof of internet; a ping reply or an adapter that's up
    // means there's a network but no usable internet (captive portal, DNS, etc.).
    public static NetStatus Classify(bool httpOk, bool pingOk, bool networkUp) =>
        httpOk ? NetStatus.Online :
        (networkUp || pingOk) ? NetStatus.NoInternet :
        NetStatus.Offline;

    static async Task<(bool ok, string msg)> PingAsync(CancellationToken ct)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(PingTarget, PingTimeout, cancellationToken: ct);
            return r.Status == IPStatus.Success
                ? (true, $"{r.RoundtripTime}ms")
                : (false, r.Status.ToString());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { return (false, ex.GetType().Name); }
    }

    static async Task<(bool ok, string msg)> HttpCheckAsync(CancellationToken ct)
    {
        try
        {
            // Microsoft's connectivity endpoint returns a tiny fixed body — ideal as a probe.
            using var req = new HttpRequestMessage(
                HttpMethod.Get, "http://www.msftconnecttest.com/connecttest.txt");
            using var r = await Http.SendAsync(req, ct);
            return r.IsSuccessStatusCode ? (true, "ok") : (false, $"HTTP {(int)r.StatusCode}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (false, "timed out"); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { return (false, ex.GetType().Name); }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
