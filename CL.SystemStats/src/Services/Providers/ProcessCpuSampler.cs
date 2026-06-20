using System.Diagnostics;
using CL.SystemStats.Models;

namespace CL.SystemStats.Services.Providers;

/// <summary>
/// Computes per-process CPU usage by sampling each process's total processor time
/// twice over a short interval and dividing the delta by elapsed wall-clock time
/// (normalised by logical core count). Shared by the Windows and Linux providers so
/// "top processes by CPU" reflects real activity rather than a constant 0.
/// </summary>
internal static class ProcessCpuSampler
{
    /// <summary>
    /// Takes a baseline of total processor time per process, waits
    /// <paramref name="sampleMs"/>, then returns the <paramref name="topCount"/> processes
    /// with the highest CPU usage over that window, with <c>CpuUsagePercent</c> populated.
    /// </summary>
    /// <param name="baseline">The current process list (already mapped), used to know which PIDs to sample and to carry the non-CPU fields.</param>
    /// <param name="sampleMs">Sampling window in milliseconds (from configuration).</param>
    /// <param name="topCount">How many processes to return.</param>
    public static async Task<IReadOnlyList<ProcessStats>> TopByCpuAsync(
        IReadOnlyList<ProcessStats> baseline,
        int sampleMs,
        int topCount)
    {
        var coreCount = Math.Max(1, Environment.ProcessorCount);

        // First sample: capture (pid -> total CPU time) and the start timestamp.
        var first = new Dictionary<int, TimeSpan>(baseline.Count);
        foreach (var p in baseline)
        {
            if (TryGetCpuTime(p.ProcessId, out var t))
                first[p.ProcessId] = t;
        }

        var sw = Stopwatch.StartNew();
        await Task.Delay(Math.Max(1, sampleMs)).ConfigureAwait(false);
        sw.Stop();

        var wallMs = sw.Elapsed.TotalMilliseconds;
        if (wallMs <= 0) wallMs = sampleMs;

        var computed = new List<ProcessStats>(baseline.Count);
        foreach (var p in baseline)
        {
            double cpuPercent = 0.0;
            if (first.TryGetValue(p.ProcessId, out var t0) && TryGetCpuTime(p.ProcessId, out var t1))
            {
                var cpuDeltaMs = (t1 - t0).TotalMilliseconds;
                // Percentage of total machine capacity: delta CPU time over (wall time × cores).
                cpuPercent = cpuDeltaMs / (wallMs * coreCount) * 100.0;
                if (cpuPercent < 0) cpuPercent = 0;       // process restarted / counter reset
                if (cpuPercent > 100) cpuPercent = 100;   // clamp rounding overshoot
            }

            computed.Add(p with { CpuUsagePercent = Math.Round(cpuPercent, 2) });
        }

        return computed
            .OrderByDescending(p => p.CpuUsagePercent)
            .Take(topCount)
            .ToList();
    }

    private static bool TryGetCpuTime(int pid, out TimeSpan cpuTime)
    {
        cpuTime = TimeSpan.Zero;
        try
        {
            using var proc = Process.GetProcessById(pid);
            cpuTime = proc.TotalProcessorTime;
            return true;
        }
        catch
        {
            // Process exited between samples, or access denied — skip it.
            return false;
        }
    }
}
