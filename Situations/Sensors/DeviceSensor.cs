using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Enumeration;

// External devices (Bluetooth, USB, monitors, docks) and whether each is connected: a DeviceWatcher
// over device containers, the same list as Settings > Bluetooth & devices. Paired Bluetooth devices
// and unplugged USB devices stay in the list as not connected. Changed is raised on a background thread.
sealed class DeviceSensor : IDisposable
{
    // Containers other than the PC itself.
    const string ExternalDevices = "System.Devices.LocalMachine:=System.StructuredQueryType.Boolean#False";
    const string ConnectedProperty = "System.Devices.Connected";

    readonly DeviceWatcher _watcher;
    readonly Dictionary<string, (string Name, bool Connected)> _containers = [];
    readonly object _lock = new();
    bool _enumerated;

    public DeviceSensor()
    {
        // A List, not an array: CsWinRT marshals it as IIterable<string> in trimmed builds too.
        _watcher = DeviceInformation.CreateWatcher(ExternalDevices, new List<string> { ConnectedProperty }, DeviceInformationKind.DeviceContainer);
        _watcher.Added += OnAdded;
        _watcher.Updated += OnUpdated;
        _watcher.Removed += OnRemoved;
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
        _watcher.Start();
    }

    public event Action? Changed;

    // Names of the connected devices; null until the first enumeration completes.
    public IReadOnlyList<string>? Devices { get; private set; }

    void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        lock (_lock)
            _containers[info.Id] = (info.Name, IsConnected(info.Properties));
        Publish();
    }

    void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_lock)
        {
            if (!_containers.TryGetValue(update.Id, out var container) || !update.Properties.ContainsKey(ConnectedProperty))
                return;

            _containers[update.Id] = container with { Connected = IsConnected(update.Properties) };
        }
        Publish();
    }

    void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_lock)
            _containers.Remove(update.Id);
        Publish();
    }

    void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        _enumerated = true;
        Publish();
    }

    // The watcher reports each container on its own during enumeration; wait for the full list.
    void Publish()
    {
        if (!_enumerated)
            return;

        List<string> devices;
        lock (_lock)
        {
            devices = _containers.Values
                .Where(c => c.Connected && IsUserFacing(c.Name))
                .Select(c => c.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        if (Devices is { } current && current.SequenceEqual(devices))
            return;

        Devices = devices;
        Changed?.Invoke();
    }

    static bool IsConnected(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(ConnectedProperty, out object? value) && value is true;

    // Windows also lists a few software endpoints as containers ("Windows.Devices.Sms.SmsDevice").
    static bool IsUserFacing(string name) =>
        !string.IsNullOrWhiteSpace(name) && !name.StartsWith("Windows.", StringComparison.Ordinal);

    public void Dispose()
    {
        _watcher.Added -= OnAdded;
        _watcher.Updated -= OnUpdated;
        _watcher.Removed -= OnRemoved;
        _watcher.EnumerationCompleted -= OnEnumerationCompleted;
        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            _watcher.Stop();
    }
}
