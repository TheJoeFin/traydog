using System.Collections.Generic;
using System.Linq;

enum ResourceAlertAction { Dismiss, IgnoreOnce, IgnoreAlways, WatchAndDismiss }

static class ResourceAlertList
{
    public const int Limit = 100;

    public static IReadOnlyList<ResourceAlert> Add(IReadOnlyList<ResourceAlert> alerts, ResourceAlert alert, out bool overflow)
    {
        overflow = false;
        if (alerts.Any(a => a.Usage.Key == alert.Usage.Key))
            return [.. alerts.Select(a => a.Usage.Key == alert.Usage.Key
                ? alert with { Kinds = a.Kinds | alert.Kinds, History = a.History, Watching = a.Watching,
                    Availability = a.Availability } : a)];

        // Never evict a row someone may be about to act on.
        if (alerts.Count >= Limit)
        {
            overflow = true;
            return alerts;
        }
        return [.. alerts, alert];
    }

    public static ResourceAlert[] Selected(IReadOnlyList<ResourceAlert> alerts, IEnumerable<ProcessKey> keys)
    {
        var selected = keys.ToHashSet();
        return [.. alerts.Where(a => selected.Contains(a.Usage.Key))];
    }

    public static ResourceAlert[] Watch(IReadOnlyList<ResourceAlert> alerts, IEnumerable<ProcessKey> keys)
    {
        var selected = keys.ToHashSet();
        return [.. alerts.Select(a => selected.Contains(a.Usage.Key) && a.CanWatch ? a with { Watching = true } : a)];
    }

    public static ResourceOptions? IgnoreOptions(ResourceOptions options, IReadOnlyList<ResourceAlert> alerts, out string? error)
    {
        error = null;
        var names = new HashSet<string>(options.IgnoredNames, System.StringComparer.OrdinalIgnoreCase);
        foreach (var alert in alerts)
        {
            if (ProcessName.Normalize(alert.Usage.Name) is not { } name)
            {
                error = $"An ignore rule could not be created for {alert.Usage.Name}. No rules were changed.";
                return null;
            }
            names.Add(name);
        }
        if (names.Count > 500)
        {
            error = "There can be at most 500 ignore rules. Remove a rule before ignoring more names.";
            return null;
        }
        return (options with { IgnoredNames = [.. names] }).Sanitize();
    }
}
