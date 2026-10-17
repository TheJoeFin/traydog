using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

sealed record ProcessScan(IReadOnlyList<ProcessSample> Samples, IReadOnlySet<int> AlivePids, int UnavailableCount);

// No WMI, performance-counter instances, retained Process handles, modules, or command lines.
// GetProcesses obtains one system snapshot; fast confirmation opens only the suspect PIDs.
static class ProcessSampler
{
    public static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public static ProcessScan Read(IReadOnlyList<ProcessKey>? targets = null)
    {
        var samples = new List<ProcessSample>();
        var alive = new HashSet<int>();
        int unavailable = 0;
        if (targets is null)
        {
            foreach (var process in Process.GetProcesses())
                ReadOne(process, null);
        }
        else
        {
            foreach (var target in targets)
            {
                try { ReadOne(Process.GetProcessById(target.Pid), target); }
                catch (ArgumentException) { } // exited since the previous sample
            }
        }
        return new(samples, alive, unavailable);

        void ReadOne(Process process, ProcessKey? expected)
        {
            using (process)
            {
                try
                {
                    alive.Add(process.Id);
                    if (process.Id is 0 or 4)
                        return;
                    var key = new ProcessKey(process.Id, process.StartTime.ToUniversalTime().Ticks);
                    if (expected is { } e && key != e)
                        return; // PID was reused
                    samples.Add(new(key, process.ProcessName, process.TotalProcessorTime.Ticks,
                        process.WorkingSet64, null, null, Now));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    unavailable++; // protected/exited processes are reported in the UI, not treated as zero
                }
            }
        }
    }
}
