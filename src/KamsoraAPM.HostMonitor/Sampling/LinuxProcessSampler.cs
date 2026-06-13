// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Host.V1;
using KamsoraAPM.HostMonitor.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// Linux <see cref="IProcessSampler"/>. Per-process CPU is the delta of
/// utime+stime from <c>/proc/[pid]/stat</c> between samples, normalized by
/// elapsed wall-clock and logical-core count (0..1 of the whole machine, matching
/// the Windows sampler). RSS comes from the same line. Returns the top-N by CPU.
///
/// Limitations (parity with the Windows cut): user, runtime version, service
/// name, and handle/fd count are left empty/zero.
/// </summary>
public sealed class LinuxProcessSampler : IProcessSampler
{
    // USER_HZ: the unit of utime/stime. 100 on effectively all modern Linux
    // kernels (independent of CONFIG_HZ), so jiffies-to-seconds is value / 100.
    private const double ClockTicksPerSec = 100d;

    private readonly HostMonitorOptions _options;
    private readonly ILogger<LinuxProcessSampler> _logger;
    private readonly Dictionary<int, ulong> _previousTicks = new();
    private readonly int _logicalCores = Math.Max(1, Environment.ProcessorCount);
    private readonly ulong _pageSize = (ulong)Environment.SystemPageSize;
    private DateTime _lastUtc = DateTime.UtcNow;

    public LinuxProcessSampler(IOptions<HostMonitorOptions> options, ILogger<LinuxProcessSampler> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public ValueTask<IReadOnlyList<ProcessSample>> SampleAsync(CancellationToken cancellationToken)
    {
        var nowUtc    = DateTime.UtcNow;
        var elapsed   = Math.Max(0.001, (nowUtc - _lastUtc).TotalSeconds);
        var nextTicks = new Dictionary<int, ulong>(_previousTicks.Count);
        var rows      = new List<ProcessSample>(256);

        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;

                var stat = TryReadAllText(Path.Combine(dir, "stat"));
                if (stat is null) continue;
                if (!TryParseStat(stat, out var comm, out var totalTicks, out var rssPages, out var threads)) continue;

                nextTicks[pid] = totalTicks;

                double cpuUtil = 0d;
                if (_previousTicks.TryGetValue(pid, out var prevTicks) && totalTicks >= prevTicks)
                {
                    var cpuSec = (totalTicks - prevTicks) / ClockTicksPerSec;
                    cpuUtil = Math.Clamp(cpuSec / elapsed / _logicalCores, 0d, 1d);
                }

                rows.Add(new ProcessSample
                {
                    Pid            = (uint)pid,
                    Command        = comm,
                    User           = string.Empty,
                    RuntimeVersion = string.Empty,
                    ServiceName    = string.Empty,
                    CpuUtilization = cpuUtil,
                    RssBytes       = rssPages * _pageSize,
                    ThreadCount    = threads,
                    HandleCount    = 0,
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM HostMonitor: failed to sample processes.");
        }

        _previousTicks.Clear();
        foreach (var kv in nextTicks) _previousTicks[kv.Key] = kv.Value;
        _lastUtc = nowUtc;

        // Sort by CPU descending, trim to top-N. The first tick has no baseline
        // (all zeros); the next produces real utilization.
        rows.Sort(static (a, b) => b.CpuUtilization.CompareTo(a.CpuUtilization));
        if (rows.Count > _options.TopProcesses)
            rows = rows.GetRange(0, _options.TopProcesses);

        return ValueTask.FromResult<IReadOnlyList<ProcessSample>>(rows);
    }

    private static string? TryReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; } // process exited mid-scan, or permission denied
    }

    // /proc/[pid]/stat: "pid (comm) state ... utime(14) stime(15) ... num_threads(20) ... rss(24) ...".
    // comm may contain spaces and parentheses, so it is bounded by the LAST ')'.
    private static bool TryParseStat(string stat, out string comm, out ulong totalTicks, out ulong rssPages, out uint threads)
    {
        comm = string.Empty; totalTicks = 0; rssPages = 0; threads = 0;

        var open  = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open < 0 || close <= open) return false;

        comm = stat.Substring(open + 1, close - open - 1);

        // After ')', the first token is field 3 (state); field N is at index N-3.
        var rest = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ulong R(int field) => (field - 3) >= 0 && (field - 3) < rest.Length && ulong.TryParse(rest[field - 3], out var v) ? v : 0UL;

        totalTicks = R(14) + R(15); // utime + stime
        threads    = (uint)R(20);
        rssPages   = R(24);
        return true;
    }
}
