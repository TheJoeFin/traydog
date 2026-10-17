using System;
using System.Collections.Generic;
using System.Linq;

// Uses monotonic time, not the wall clock. Each resource must stay high independently; alternating
// CPU and RAM spikes must not add up to a sustained problem. Only candidates get fast rechecks.
sealed class ResourcePolicy
{
    public const int ReminderSeconds = 15 * 60;
    static readonly ResourceKind[] MeasuredKinds = [ResourceKind.Cpu, ResourceKind.Ram, ResourceKind.Disk, ResourceKind.Network];
    readonly Dictionary<ProcessKey, Entry> _entries = [];

    sealed class Entry
    {
        public readonly Dictionary<ResourceKind, double> Since = [];
        public double LastAt;
        public double NextAlertAt;
        public bool Ignored;
        public bool SightSeen;
    }

    public IReadOnlyList<ProcessKey> Candidates =>
        _entries.Where(p => !p.Value.Ignored && p.Value.Since.Count > 0 && p.Value.NextAlertAt <= p.Value.LastAt)
            .Select(p => p.Key).ToArray();

    public ResourceKind Observe(ResourceUsage usage, ResourceOptions options, double now)
    {
        if (ProcessName.Contains(options.IgnoredNames, usage.Name))
            return ResourceKind.None;

        ResourceKind high = usage.High(options);
        if (!_entries.TryGetValue(usage.Key, out var entry))
        {
            if (high == ResourceKind.None)
                return ResourceKind.None;
            _entries[usage.Key] = entry = new Entry();
        }
        if (entry.Ignored)
            return ResourceKind.None;

        if (now - entry.LastAt > Math.Max(3, options.ConfirmSeconds * 3) || now < entry.LastAt)
            entry.Since.Clear(); // sleep, a failed sample, or a deliberately slow scan isn't confirmation
        entry.LastAt = now;

        ResourceKind sustained = ResourceKind.None;
        foreach (var kind in MeasuredKinds)
        {
            if (!high.HasFlag(kind))
            {
                entry.Since.Remove(kind);
                continue;
            }
            if (!entry.Since.TryGetValue(kind, out double since))
                entry.Since[kind] = since = now;
            if (now - since >= options.SustainedSeconds)
                sustained |= kind;
        }

        if (sustained == ResourceKind.None || now < entry.NextAlertAt)
            return ResourceKind.None;
        entry.NextAlertAt = now + ReminderSeconds;
        return sustained;
    }

    public bool OnSight(ProcessKey key, string name, ResourceOptions options)
    {
        if (!ProcessName.Contains(options.OnSightNames, name) || ProcessName.Contains(options.IgnoredNames, name))
            return false;
        if (!_entries.TryGetValue(key, out var entry))
            _entries[key] = entry = new Entry();
        if (entry.Ignored || entry.SightSeen)
            return false;
        entry.SightSeen = true;
        return true;
    }

    public void Dismiss(ProcessKey key, double now)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.NextAlertAt = now + ReminderSeconds;
            entry.Since.Clear();
        }
    }

    public void IgnoreOnce(ProcessKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
            _entries[key] = entry = new Entry();
        entry.Ignored = true;
        entry.Since.Clear();
    }

    public void ForgetMissing(IReadOnlySet<ProcessKey> alive, IReadOnlySet<int>? unreadablePids = null)
    {
        foreach (var key in _entries.Keys.Where(k => !alive.Contains(k) && !(unreadablePids?.Contains(k.Pid) ?? false)).ToArray())
            _entries.Remove(key);
    }

    public void Forget(ProcessKey key) => _entries.Remove(key);
    public void ResetConfirmation(ProcessKey key)
    {
        if (_entries.TryGetValue(key, out var entry))
            entry.Since.Clear();
    }
    public void ResetConfirmation()
    {
        foreach (var entry in _entries.Values)
            entry.Since.Clear();
    }
}
