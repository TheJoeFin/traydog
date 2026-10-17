using System;
using Microsoft.Windows.System.Power;    // Windows App SDK PowerManager (desktop-friendly events)

// Battery charge and whether the PC is plugged in, updated on the Windows App SDK power events.
// Changed is raised on a background thread.
sealed class PowerSensor : IDisposable
{
    public PowerSensor()
    {
        Power = Read();
        PowerManager.RemainingChargePercentChanged += OnPowerChanged;
        PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
        PowerManager.BatteryStatusChanged += OnPowerChanged;
    }

    public event Action? Changed;

    public PowerState? Power { get; private set; }

    static PowerState? Read()
    {
        try
        {
            bool hasBattery = PowerManager.BatteryStatus != BatteryStatus.NotPresent;
            return new PowerState(
                hasBattery ? PowerManager.RemainingChargePercent : null,
                PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent);
        }
        catch
        {
            return null;
        }
    }

    void OnPowerChanged(object? sender, object e)
    {
        var power = Read();
        if (power == Power)
            return;

        Power = power;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        PowerManager.RemainingChargePercentChanged -= OnPowerChanged;
        PowerManager.PowerSupplyStatusChanged -= OnPowerChanged;
        PowerManager.BatteryStatusChanged -= OnPowerChanged;
    }
}
