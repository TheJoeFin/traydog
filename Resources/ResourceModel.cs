using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

[Flags]
enum ResourceKind { None = 0, Cpu = 1, Ram = 2, Disk = 4, Network = 8, OnSight = 16 }

readonly record struct ProcessKey(int Pid, long StartedUtcTicks);

// Constructor defaults also apply to missing JSON fields with the source-generated serializer.
sealed record ResourceOptions(
    int ScanSeconds = 30,
    int ConfirmSeconds = 1,
    int SustainedSeconds = 20,
    bool CpuEnabled = true,
    bool RamEnabled = true,
    bool DiskEnabled = true,
    bool NetworkEnabled = true,
    double CpuPercent = 80,
    double RamMegabytes = 1024,
    double DiskMegabytesPerSecond = 50,
    double NetworkMegabytesPerSecond = 10,
    string[]? IgnoredNames = null,
    string[]? OnSightNames = null)
{
    public string[] IgnoredNames { get; init; } = IgnoredNames ?? [];
    public string[] OnSightNames { get; init; } = OnSightNames ?? [];

    public ResourceKind IoKinds(bool connected) => !connected ? ResourceKind.None
        : (DiskEnabled ? ResourceKind.Disk : ResourceKind.None) | (NetworkEnabled ? ResourceKind.Network : ResourceKind.None);

    public ResourceOptions Sanitize() => this with
    {
        ScanSeconds = Math.Clamp(ScanSeconds, 5, 3600),
        ConfirmSeconds = Math.Clamp(ConfirmSeconds, 1, 10),
        SustainedSeconds = Math.Clamp(SustainedSeconds, 5, 300),
        CpuPercent = Limit(CpuPercent, 1, 100, 80),
        RamMegabytes = Limit(RamMegabytes, 1, 1_048_576, 1024),
        DiskMegabytesPerSecond = Limit(DiskMegabytesPerSecond, 0.1, 1_000_000, 50),
        NetworkMegabytesPerSecond = Limit(NetworkMegabytesPerSecond, 0.1, 1_000_000, 10),
        IgnoredNames = Names(IgnoredNames),
        OnSightNames = Names(OnSightNames).Except(Names(IgnoredNames), StringComparer.OrdinalIgnoreCase).ToArray(),
    };

    static double Limit(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    static string[] Names(string[]? values) =>
        (values ?? []).Select(ProcessName.Normalize).Where(n => n is not null).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(500).Order(StringComparer.OrdinalIgnoreCase).ToArray();
}

static class ProcessName
{
    public static string? Normalize(string? value)
    {
        string name = (value ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.Length is 0 or > 250 || name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            name.Any(char.IsControl))
            return null;
        return name.ToLowerInvariant();
    }

    public static bool Contains(IEnumerable<string> names, string name) =>
        names.Contains(Normalize(name) ?? "", StringComparer.OrdinalIgnoreCase);
}

sealed record ProcessSample(ProcessKey Key, string Name, long CpuTicks, long RamBytes,
    long? DiskBytes, long? NetworkBytes, double AtSeconds);

sealed record ResourceUsage(ProcessKey Key, string Name, double? CpuPercent, long RamBytes,
    double? DiskBytesPerSecond, double? NetworkBytesPerSecond)
{
    public static ResourceUsage From(ProcessSample current, ProcessSample? previous, int processorCount)
    {
        double seconds = previous is not null && previous.Key == current.Key ? current.AtSeconds - previous.AtSeconds : 0;
        return new(current.Key, current.Name,
            Rate(current.CpuTicks, previous?.CpuTicks, seconds) is { } cpu
                ? Math.Clamp(cpu / TimeSpan.TicksPerSecond / Math.Max(1, processorCount) * 100, 0, 100) : null,
            current.RamBytes,
            Rate(current.DiskBytes, previous?.DiskBytes, seconds),
            Rate(current.NetworkBytes, previous?.NetworkBytes, seconds));
    }

    static double? Rate(long? current, long? previous, double seconds) =>
        seconds > 0 && current is { } c && previous is { } p && c >= p ? (c - p) / seconds : null;

    public ResourceKind High(ResourceOptions options)
    {
        const double megabyte = 1024 * 1024;
        ResourceKind high = ResourceKind.None;
        if (options.CpuEnabled && CpuPercent >= options.CpuPercent) high |= ResourceKind.Cpu;
        if (options.RamEnabled && RamBytes >= options.RamMegabytes * megabyte) high |= ResourceKind.Ram;
        if (options.DiskEnabled && DiskBytesPerSecond >= options.DiskMegabytesPerSecond * megabyte) high |= ResourceKind.Disk;
        if (options.NetworkEnabled && NetworkBytesPerSecond >= options.NetworkMegabytesPerSecond * megabyte) high |= ResourceKind.Network;
        return high;
    }
}

sealed record ResourceAlert(ResourceUsage Usage, ResourceKind Kinds, DateTimeOffset At, int SustainedSeconds = 0)
{
    public bool Watching { get; init; }
    public ProcessAvailability Availability { get; init; } = ProcessAvailability.Available;
    public IReadOnlyList<ResourceReading> History { get; init; } = [];
    public bool IsStopped => Availability == ProcessAvailability.Exited;
    public bool CanWatch => !Watching && !IsStopped;
    public ResourceUsage? Current => Availability == ProcessAvailability.Exited
        ? History.LastOrDefault(p => p.Usage is not null)?.Usage
        : History.Count > 0 ? History[^1].Usage : null;
}

static class ResourceText
{
    public static Uri SearchUri(string name) =>
        new("https://www.bing.com/search?q=" + Uri.EscapeDataString((ProcessName.Normalize(name) ?? name) + ".exe"));

    public static string Kinds(ResourceKind kinds) => string.Join(", ",
        new[] { ResourceKind.Cpu, ResourceKind.Ram, ResourceKind.Disk, ResourceKind.Network, ResourceKind.OnSight }
            .Where(k => kinds.HasFlag(k)).Select(k => k switch
            {
                ResourceKind.Cpu => "CPU", ResourceKind.Ram => "RAM", ResourceKind.OnSight => "Alert on sight", _ => k.ToString()
            }));

    public static string Cpu(double? value) => value is { } v ? $"{v:0.#}%" : "Unavailable";
    public static string Memory(long bytes) => $"{bytes / (1024d * 1024):N0} MB";
    public static string Rate(double? bytes) => bytes is { } v ? $"{v / (1024 * 1024):N1} MB/s" : "Unavailable";

    public static string Status(ResourceAlert alert, bool running, ResourceOptions options) =>
        alert.IsStopped ? "Tracking stopped: process closed"
        : !running ? "Paused"
        : alert.Availability == ProcessAvailability.Unavailable ? "Process unreadable"
        : alert.Current is not { } current ? "Waiting for a sample"
        : current.High(options) != ResourceKind.None ? "Usage above threshold"
        : alert.Usage.High(options) != ResourceKind.None ? "Usage has dropped below thresholds" : "Below thresholds";

    public static string Coverage(ResourceOptions options, bool ioConnected)
    {
        var checks = new List<string>();
        if (options.CpuEnabled) checks.Add("CPU");
        if (options.RamEnabled) checks.Add("RAM");
        var io = options.IoKinds(ioConnected);
        if (io.HasFlag(ResourceKind.Disk)) checks.Add("Disk");
        if (io.HasFlag(ResourceKind.Network)) checks.Add("Network");
        if (options.OnSightNames.Length > 0) checks.Add("Name rules");
        bool needsIo = !ioConnected && (options.DiskEnabled || options.NetworkEnabled);
        string active = string.Join(" / ", checks);
        return needsIo ? (active.Length == 0 ? "I/O permission needed" : $"{active} · I/O permission needed")
            : active.Length == 0 ? "No alert checks enabled" : active;
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ResourceOptions))]
[JsonSerializable(typeof(CollectorMessage))]
[JsonSerializable(typeof(CollectorRequest))]
partial class ResourceJson : JsonSerializerContext;

sealed record IoSample(ProcessKey Key, long DiskBytes, long NetworkBytes);
sealed record CollectorRequest(string Command, string[]? Names = null, int[]? TargetPids = null, int[]? AlivePids = null);
sealed record CollectorMessage(string Type, IoSample[]? Samples = null, ProcessKey? Key = null,
    string? Name = null, string? Error = null, long EventsLost = 0);
