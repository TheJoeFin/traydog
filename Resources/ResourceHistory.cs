using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

enum ProcessAvailability { Available, Unavailable, Exited }

sealed record ResourceReading(double AtSeconds, DateTimeOffset At, ResourceUsage? Usage);

static class ResourceHistory
{
    public const int Limit = 120;
    public const double ChartWidth = 160;
    public const double ChartHeight = 28;
    public const double BarWidth = 0.8;

    public static ProcessKey[] Targets(IReadOnlyList<ProcessKey> candidates, IReadOnlyList<ResourceAlert> alerts) =>
        [.. candidates.Concat(alerts.Where(a => a.Availability != ProcessAvailability.Exited).Select(a => a.Usage.Key)).Distinct()];

    public static ResourceAlert[] Update(IReadOnlyList<ResourceAlert> alerts,
        IReadOnlyDictionary<ProcessKey, ResourceUsage> samples, IReadOnlySet<int> alivePids,
        IReadOnlyList<ProcessKey>? targets, double now, DateTimeOffset at)
    {
        var checkedKeys = targets?.ToHashSet();
        var sampledPids = samples.Keys.Select(k => k.Pid).ToHashSet();
        return alerts.Select(alert =>
        {
            var key = alert.Usage.Key;
            if (alert.Availability == ProcessAvailability.Exited || checkedKeys is not null && !checkedKeys.Contains(key))
                return alert;
            samples.TryGetValue(key, out var usage);
            var availability = usage is not null ? ProcessAvailability.Available
                : alivePids.Contains(key.Pid) && !sampledPids.Contains(key.Pid)
                    ? ProcessAvailability.Unavailable : ProcessAvailability.Exited;
            if (availability == ProcessAvailability.Exited)
                return alert with { Availability = availability };
            return Append(alert, new(now, at, usage)) with { Availability = availability };
        }).ToArray();
    }

    public static ResourceAlert Append(ResourceAlert alert, ResourceReading reading)
    {
        if (alert.History.Count > 0 && reading.AtSeconds <= alert.History[^1].AtSeconds)
            return alert;
        return alert with { History = alert.History.TakeLast(Limit - 1).Append(reading).ToArray() };
    }

    public static ResourceAlert[] Break(IReadOnlyList<ResourceAlert> alerts, double now, DateTimeOffset at) =>
        alerts.Select(a => a.Availability == ProcessAvailability.Exited ? a : Append(a, new(now, at, null))).ToArray();

    public static ResourceAlert[] Exit(IReadOnlyList<ResourceAlert> alerts, ProcessKey key) =>
        alerts.Select(a => a.Usage.Key == key ? a with { Availability = ProcessAvailability.Exited } : a).ToArray();

    public static double? Value(ResourceUsage? usage, ResourceKind kind) => kind switch
    {
        ResourceKind.Cpu => usage?.CpuPercent,
        ResourceKind.Ram => usage?.RamBytes,
        ResourceKind.Disk => usage?.DiskBytesPerSecond,
        ResourceKind.Network => usage?.NetworkBytesPerSecond,
        _ => null
    };

    public static string Format(double? value, ResourceKind kind) => value is not { } v ? "Unavailable" : kind switch
    {
        ResourceKind.Cpu => ResourceText.Cpu(v),
        ResourceKind.Ram => ResourceText.Memory((long)v),
        _ => ResourceText.Rate(v)
    };

    public static string Trend(ResourceAlert alert, ResourceKind kind)
    {
        double? current = Value(alert.Current, kind);
        double? detected = Value(alert.Usage, kind);
        if (current is not { } c || detected is not { } d)
            return "Not enough data";
        string direction = c > d ? "Up" : c < d ? "Down" : "Unchanged";
        return $"{direction} from {Format(d, kind)} at detection";
    }

    // Fixed slots keep each check distinct: new bars enter at the right and push older ones left.
    // Missing readings reserve an empty slot; a real zero still gets a short, visible bar.
    public static string BarPath(IReadOnlyList<ResourceReading> history, ResourceKind kind)
    {
        if (history.Count == 0)
            return "";
        var values = history.Select(p => Value(p.Usage, kind)).ToArray();
        double maximum = kind == ResourceKind.Cpu ? 100 : Math.Max(1, values.OfType<double>()
            .Where(v => v >= 0 && double.IsFinite(v)).DefaultIfEmpty(1).Max());
        double step = (ChartWidth - 4) / Limit;
        double baseline = ChartHeight - 2;
        var path = new StringBuilder();
        for (int i = 0; i < history.Count; i++)
        {
            if (values[i] is not { } value || !double.IsFinite(value) || value < 0)
                continue;
            double x = 2 + (Limit - history.Count + i + 0.5) * step;
            double height = Math.Max(1, (ChartHeight - 4) * Math.Clamp(value / maximum, 0, 1));
            path.Append(CultureInfo.InvariantCulture, $" M{x:0.##},{baseline:0.##} L{x:0.##},{baseline - height:0.##}");
        }
        return path.ToString();
    }
}
