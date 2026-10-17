using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

// Runs only in a separate, explicitly elevated copy of this executable, before any Reactor/WinUI
// startup. No files, payloads, stacks, symbols, socket addresses, WMI, or per-event IPC.
static class ResourceCollector
{
    sealed class Totals(ProcessKey key)
    {
        public readonly ProcessKey Key = key;
        public long Disk;
        public long Network;
    }

    public static async Task RunAsync(string pipeName, int parentPid)
    {
        if (!pipeName.StartsWith(CollectorPipe.Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(pipeName[CollectorPipe.Prefix.Length..], "N", out _) || parentPid <= 0)
        {
            CrashLog.Write(new ArgumentException("Invalid resource collector pipe or parent."));
            return;
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            CollectorPipe.VerifyPeer(pipe, parentPid, server: false);
            using var parent = Process.GetProcessById(parentPid);
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Resource collector parent executable does not match.");

            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await CollectAsync(reader, writer, pipeName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or Win32Exception or ArgumentException)
        {
            CrashLog.Write(ex);
        }
    }

    static async Task CollectAsync(StreamReader reader, StreamWriter writer, string sessionName)
    {
        var output = Channel.CreateBounded<CollectorMessage>(256);
        object gate = new();
        var totals = new Dictionary<int, Totals>();
        var watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watchedInstances = new Dictionary<int, ProcessKey>();
        long overflow = 0;

        using var session = new TraceEventSession(sessionName, TraceEventSessionOptions.NoRestartOnCreate)
        {
            BufferSizeMB = 2,
            StopOnDispose = true,
        };
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread |
            KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskIOInit |
            KernelTraceEventParser.Keywords.NetworkTCPIP);
        var kernel = session.Source.Kernel;

        ProcessKey Identity(int pid, long fallback = 0)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return new(pid, process.StartTime.ToUniversalTime().Ticks);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                return new(pid, fallback);
            }
        }

        void Add(int pid, long bytes, bool disk)
        {
            if (pid <= 0 || bytes <= 0)
                return;
            lock (gate)
            {
                if (!totals.TryGetValue(pid, out var value))
                {
                    if (totals.Count >= 16384)
                    {
                        Interlocked.Increment(ref overflow);
                        return;
                    }
                    totals[pid] = value = new(Identity(pid));
                }
                if (disk) value.Disk += bytes;
                else value.Network += bytes;
            }
        }

        kernel.ProcessStart += data =>
        {
            string? name = ProcessName.Normalize(Path.GetFileName(data.ImageFileName));
            bool matches;
            lock (gate)
            {
                totals.Remove(data.ProcessID);
                matches = name is not null && watched.Contains(name);
            }
            if (matches)
            {
                var key = Identity(data.ProcessID, data.TimeStamp.ToUniversalTime().Ticks);
                lock (gate)
                    watchedInstances[data.ProcessID] = key;
                if (!output.Writer.TryWrite(new("start", Key: key, Name: name)))
                    Interlocked.Increment(ref overflow);
            }
        };
        kernel.ProcessStop += data =>
        {
            ProcessKey? stopped = null;
            lock (gate)
            {
                totals.Remove(data.ProcessID);
                if (watchedInstances.Remove(data.ProcessID, out var key))
                    stopped = key;
            }
            if (stopped is { } instance && !output.Writer.TryWrite(new("exit", Key: instance)))
                Interlocked.Increment(ref overflow);
        };
        kernel.DiskIORead += data => Add(data.ProcessID, data.TransferSize, disk: true);
        kernel.DiskIOWrite += data => Add(data.ProcessID, data.TransferSize, disk: true);
        kernel.TcpIpSend += data => Add(data.ProcessID, data.size, disk: false);
        kernel.TcpIpRecv += data => Add(data.ProcessID, data.size, disk: false);
        kernel.TcpIpSendIPV6 += data => Add(data.ProcessID, data.size, disk: false);
        kernel.TcpIpRecvIPV6 += data => Add(data.ProcessID, data.size, disk: false);
        kernel.UdpIpSend += data => Add(data.ProcessID, data.size, disk: false);
        kernel.UdpIpRecv += data => Add(data.ProcessID, data.size, disk: false);
        kernel.UdpIpSendIPV6 += data => Add(data.ProcessID, data.size, disk: false);
        kernel.UdpIpRecvIPV6 += data => Add(data.ProcessID, data.size, disk: false);

        Task? trace = null;
        var send = Task.Run(async () =>
        {
            await foreach (var message in output.Reader.ReadAllAsync())
                await writer.WriteLineAsync(JsonSerializer.Serialize(message, ResourceJson.Default.CollectorMessage));
        });
        try
        {
            trace = Task.Factory.StartNew(() => session.Source.Process(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await output.Writer.WriteAsync(new("ready"));
            while (true)
            {
                var read = reader.ReadLineAsync();
                if (await Task.WhenAny(read, trace, send) != read)
                    throw new IOException("Resource tracing stopped unexpectedly.");
                string? line = await read;
                if (line is null)
                    break; // parent closed its pipe or exited
                var request = JsonSerializer.Deserialize(line, ResourceJson.Default.CollectorRequest)
                    ?? throw new InvalidDataException("Empty resource collector request.");
                if (request.Command == "watch")
                {
                    lock (gate)
                        watched = (request.Names ?? []).Select(ProcessName.Normalize).Where(n => n is not null)
                            .Select(n => n!).Take(500).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                else if (request.Command == "sample")
                {
                    IoSample[] snapshot;
                    lock (gate)
                    {
                        if (request.AlivePids is { } pids)
                        {
                            var alive = pids.ToHashSet();
                            foreach (int pid in totals.Keys.Where(pid => !alive.Contains(pid)).ToArray())
                                totals.Remove(pid);
                            foreach (int pid in watchedInstances.Keys.Where(pid => !alive.Contains(pid)).ToArray())
                                watchedInstances.Remove(pid);
                        }
                        // Confirmation returns just the suspects, not every process's counters.
                        var selected = request.TargetPids is { } targets
                            ? targets.Where(totals.ContainsKey).Select(pid => totals[pid])
                            : totals.Values.AsEnumerable();
                        snapshot = selected.Select(t => new IoSample(t.Key, t.Disk, t.Network)).ToArray();
                    }
                    long lost = session.EventsLost + Interlocked.Read(ref overflow);
                    await output.Writer.WriteAsync(new("sample", snapshot, EventsLost: lost));
                }
                else
                    throw new InvalidDataException("Unknown resource collector command.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException or InvalidOperationException)
        {
            CrashLog.Write(ex);
            output.Writer.TryWrite(new("error", Error: $"Disk/Network collection stopped: {ex.Message}"));
        }
        finally
        {
            session.Stop();
            if (trace is not null)
                await trace;
            output.Writer.TryComplete();
            await send;
        }
    }
}
