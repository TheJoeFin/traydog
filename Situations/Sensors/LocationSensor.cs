using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

enum LocationAccess
{
    Unknown,    // not asked yet
    Allowed,
    Denied      // off for traydog, or for the whole PC, in Settings > Privacy & security > Location
}

// Where the PC is, for place conditions. Rather than hold a live position subscription (which keeps
// the "location in use" indicator lit), it takes one quick, Wi-Fi-accurate fix at start, on Refresh
// (the engine calls it when the network changes, which is when a laptop usually changes place), and
// every RefreshInterval. Changed is raised on the UI thread.
sealed class LocationSensor : IDisposable
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(1);
    static readonly TimeSpan FixTimeout = TimeSpan.FromSeconds(30);

    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _wake = new(0, 1);

    public event Action? Changed;

    public GeoPoint? Location { get; private set; }

    public LocationAccess Access { get; private set; }

    // Call on the UI thread: asking for access can show the Windows consent prompt.
    public void Start() => _ = RunAsync(_cts.Token);

    public void Refresh()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { } // already pending
        catch (ObjectDisposedException) { }
    }

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            Access = await RequestAccessAsync();
            ct.ThrowIfCancellationRequested();
            Changed?.Invoke();
            if (Access != LocationAccess.Allowed)
                return;

            while (true)
            {
                var location = await GetFixAsync(MaximumAge);
                ct.ThrowIfCancellationRequested();
                if (location != Location)
                {
                    Location = location;
                    Changed?.Invoke();
                }

                await _wake.WaitAsync(RefreshInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed.
        }
    }

    public static async Task<LocationAccess> RequestAccessAsync()
    {
        try
        {
            return await Geolocator.RequestAccessAsync() switch
            {
                GeolocationAccessStatus.Allowed => LocationAccess.Allowed,
                GeolocationAccessStatus.Denied => LocationAccess.Denied,
                _ => LocationAccess.Unknown
            };
        }
        catch
        {
            return LocationAccess.Denied;
        }
    }

    // One fix, or null when location is off or none arrives in time. The editor's "Use my current
    // location" asks for a fresh, precise one.
    public static async Task<GeoPoint?> GetFixAsync(TimeSpan maximumAge, bool precise = false)
    {
        try
        {
            var geolocator = new Geolocator { DesiredAccuracy = precise ? PositionAccuracy.High : PositionAccuracy.Default };
            var position = await geolocator.GetGeopositionAsync(maximumAge, FixTimeout);
            var point = position.Coordinate.Point.Position;
            return new GeoPoint(point.Latitude, point.Longitude);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
