using System;

static class MonitoringLayout
{
    public const int Width = 480;
    public const int SummaryHeight = 200;
    public const int Height = 520;

    public static int PreferredHeight(int alerts) => Height + Math.Clamp(alerts, 0, 3) * 190;
}
