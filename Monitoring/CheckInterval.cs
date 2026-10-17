using System;

// The background check intervals offered in Settings, and how to describe them. A hand-edited or
// stale setting is snapped back to one of these, so it can't hammer the probe endpoints.
static class CheckInterval
{
    public static readonly TimeSpan[] InTrayChoices =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
    ];

    public static readonly TimeSpan DefaultInTray = TimeSpan.FromMinutes(1);

    public static TimeSpan SanitizeInTray(int seconds)
    {
        foreach (var choice in InTrayChoices)
        {
            if ((int)choice.TotalSeconds == seconds)
                return choice;
        }

        return DefaultInTray;
    }

    // Position of the interval in InTrayChoices, or of the default when it isn't one of them.
    public static int IndexOf(TimeSpan interval)
    {
        int index = Array.IndexOf(InTrayChoices, interval);
        return index >= 0 ? index : Array.IndexOf(InTrayChoices, DefaultInTray);
    }

    // Reads after "every": "30 seconds", "minute", "5 minutes".
    public static string Describe(TimeSpan interval) =>
        interval == TimeSpan.FromMinutes(1) ? "minute" :
        interval.TotalSeconds >= 60 && interval.TotalSeconds % 60 == 0 ? $"{(int)interval.TotalMinutes} minutes" :
        $"{(int)interval.TotalSeconds} seconds";

    // ComboBox item text in Settings.
    public static string Label(TimeSpan interval) => $"Every {Describe(interval)}";
}
