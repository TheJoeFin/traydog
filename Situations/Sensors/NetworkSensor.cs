using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Networking.Connectivity;

// The names of the networks the PC is connected to (Wi-Fi SSIDs, and the names Windows gives wired
// networks), re-read on NetworkStatusChanged. Changed is raised on a background thread.
sealed class NetworkSensor : IDisposable
{
    public NetworkSensor()
    {
        Networks = ReadNames();
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    public event Action? Changed;

    public IReadOnlyList<string> Networks { get; private set; }

    // Every connected network's name, the one with internet access first.
    public static IReadOnlyList<string> ReadNames()
    {
        try
        {
            var internet = NetworkInformation.GetInternetConnectionProfile();
            var profiles = NetworkInformation.GetConnectionProfiles()
                .OrderByDescending(p => p.ProfileName == internet?.ProfileName);

            var names = new List<string>();
            foreach (var profile in profiles)
            {
                if (profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
                    continue;

                Add(profile.ProfileName);
                if (profile.IsWlanConnectionProfile)
                    Add(profile.WlanConnectionProfileDetails?.GetConnectedSsid());
            }

            return names;

            void Add(string? name)
            {
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name.Trim());
            }
        }
        catch
        {
            return [];
        }
    }

    void OnNetworkStatusChanged(object sender)
    {
        Networks = ReadNames();
        Changed?.Invoke();
    }

    public void Dispose() => NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
}
