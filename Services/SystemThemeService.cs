using System;
using System.Threading;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.System.Registry;

// Reports the Windows (taskbar and Start) theme, which can differ from the app theme, and raises
// Changed when it flips. The popout follows this, not the app theme, so it matches the shell's own
// flyouts. (The tray icons are full-color emoji, so they read on either taskbar without a swap.)
static class SystemThemeService
{
    const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string SystemUsesLightThemeValue = "SystemUsesLightTheme";
    const string ColorPrevalenceValue = "ColorPrevalence";

    static readonly RegistryKey? PersonalizeKey = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
    static readonly AutoResetEvent KeyChanged = new(false);
    static bool _isLight = ReadFlag(SystemUsesLightThemeValue);

    static SystemThemeService()
    {
        if (PersonalizeKey is null)
            return;

        ThreadPool.RegisterWaitForSingleObject(KeyChanged, (_, _) => OnKeyChanged(), null, Timeout.Infinite, executeOnlyOnce: false);
        WatchKey();
    }

    // Raised on a background thread when the Windows theme switches between light and dark.
    public static event Action? Changed;

    // True when the Windows theme is light; false when dark or unknown.
    public static bool IsLight => _isLight;

    // True when "Show accent color on Start and taskbar" is on, which Windows only allows in dark
    // mode. Shell flyouts take an accent tint then.
    public static bool ShowsAccentColor => !IsLight && ReadFlag(ColorPrevalenceValue);

    static void OnKeyChanged()
    {
        WatchKey();
        bool isLight = ReadFlag(SystemUsesLightThemeValue);
        if (isLight != _isLight)
        {
            _isLight = isLight;
            Changed?.Invoke();
        }
    }

    // One-shot: the key must be re-watched after every notification.
    static void WatchKey() =>
        PInvoke.RegNotifyChangeKeyValue(
            PersonalizeKey!.Handle,
            false,
            REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_FILTER.REG_NOTIFY_THREAD_AGNOSTIC,
            KeyChanged.SafeWaitHandle,
            true);

    static bool ReadFlag(string name) => PersonalizeKey?.GetValue(name) is int value && value != 0;
}
