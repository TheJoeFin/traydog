using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

// Internet reachability, in increasing health.
enum NetStatus
{
    Unknown,      // not probed yet / monitoring off
    Offline,      // no usable network adapter at all
    NoInternet,   // adapter is up, but the public internet is unreachable (captive portal, DNS, etc.)
    Online        // a real HTTP probe to the internet succeeded
}

// Immutable snapshot handed to the UI: the status plus a short human-readable detail line.
record NetState(NetStatus Status, string Detail);

// Polls connectivity on a timer and on OS network-change events, modelled on
// SurfaceIssueTracking's ConnectivityMonitor (ping 8.8.8.8 + an HTTP connect test).
// StatusChanged is raised on a background thread — marshal to the UI thread before
// touching UI/state.
sealed class ConnectivityMonitor : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(15));
    private CancellationTokenSource? _cts;
    private int _checking; // 0/1 guard so timer + network-change probes don't overlap

    public event Action<NetStatus, string>? StatusChanged;

    public void Start()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    // A network-change event almost always means the answer just changed — re-probe now
    // instead of waiting for the next tick.
    private void OnNetworkChanged(object? sender, EventArgs e) => _ = CheckAsync();

    private async Task PollLoopAsync(CancellationToken ct)
    {
        await CheckAsync();
        while (!ct.IsCancellationRequested)
        {
            try { await _timer.WaitForNextTickAsync(ct); }
            catch (OperationCanceledException) { break; }
            await CheckAsync();
        }
    }

    private async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return; // a probe is already running
        try
        {
            bool networkUp = NetworkInterface.GetIsNetworkAvailable();
            var (pingOk, pingMsg) = await PingAsync("8.8.8.8");
            var (httpOk, httpMsg) = await HttpCheckAsync();

            NetStatus status =
                httpOk ? NetStatus.Online :
                (networkUp || pingOk) ? NetStatus.NoInternet :
                NetStatus.Offline;

            StatusChanged?.Invoke(status, $"ping {pingMsg} · http {httpMsg}");
        }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    private static async Task<(bool ok, string msg)> PingAsync(string host)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(host, 3000);
            return r.Status == IPStatus.Success
                ? (true, $"{r.RoundtripTime}ms")
                : (false, r.Status.ToString());
        }
        catch (Exception ex) { return (false, ex.GetType().Name); }
    }

    private static async Task<(bool ok, string msg)> HttpCheckAsync()
    {
        try
        {
            // Microsoft's connectivity endpoint returns a tiny fixed body — ideal as a probe.
            using var req = new HttpRequestMessage(
                HttpMethod.Get, "http://www.msftconnecttest.com/connecttest.txt");
            using var r = await Http.SendAsync(req);
            return r.IsSuccessStatusCode ? (true, "ok") : (false, $"HTTP {(int)r.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.GetType().Name); }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _timer.Dispose();
    }
}
