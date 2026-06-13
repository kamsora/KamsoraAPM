// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using KamsoraAPM.Contracts.Host.V1;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// Linux <see cref="ICpuMemorySampler"/>. CPU utilization is the delta of the
/// aggregate jiffy counters in <c>/proc/stat</c> between consecutive samples;
/// load averages come from <c>/proc/loadavg</c>; memory from
/// <c>/proc/meminfo</c>. Registered as a singleton so the previous /proc/stat
/// snapshot survives between ticks for the delta.
/// </summary>
public sealed class LinuxCpuMemorySampler : ICpuMemorySampler
{
    private readonly ILogger<LinuxCpuMemorySampler> _logger;
    private CpuTimes? _previous;

    public LinuxCpuMemorySampler(ILogger<LinuxCpuMemorySampler> logger) => _logger = logger;

    public ValueTask<(CpuSample cpu, MemorySample memory)> SampleAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult((ReadCpu(), ReadMemory()));

    private CpuSample ReadCpu()
    {
        var cpu = new CpuSample { LogicalCores = (uint)Environment.ProcessorCount };
        ReadLoadAverages(cpu);

        var now = ReadCpuTimes();
        if (now is null) return cpu;

        if (_previous is { } prev)
        {
            double totalDelta = now.Value.Total - prev.Total;
            if (totalDelta > 0)
            {
                cpu.UtilizationUser   = Math.Clamp((now.Value.User   - prev.User)   / totalDelta, 0d, 1d);
                cpu.UtilizationSystem = Math.Clamp((now.Value.System - prev.System) / totalDelta, 0d, 1d);
                cpu.UtilizationIowait = Math.Clamp((now.Value.Iowait - prev.Iowait) / totalDelta, 0d, 1d);
                cpu.UtilizationIdle   = Math.Clamp((now.Value.Idle   - prev.Idle)   / totalDelta, 0d, 1d);
            }
        }

        _previous = now;
        return cpu;
    }

    private CpuTimes? ReadCpuTimes()
    {
        try
        {
            // First line: "cpu  user nice system idle iowait irq softirq steal guest guest_nice"
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("cpu ", StringComparison.Ordinal)) continue;
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                ulong V(int i) => i < p.Length && ulong.TryParse(p[i], out var v) ? v : 0UL;

                // guest/guest_nice are already folded into user/nice by the kernel, so they are not added separately.
                return new CpuTimes(
                    User:   V(1) + V(2),                       // user + nice
                    System: V(3) + V(6) + V(7) + V(8),         // system + irq + softirq + steal
                    Iowait: V(5),
                    Idle:   V(4));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM HostMonitor: failed to read /proc/stat.");
        }

        return null;
    }

    private void ReadLoadAverages(CpuSample cpu)
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                if (double.TryParse(parts[0], CultureInfo.InvariantCulture, out var l1))  cpu.Load1M  = l1;
                if (double.TryParse(parts[1], CultureInfo.InvariantCulture, out var l5))  cpu.Load5M  = l5;
                if (double.TryParse(parts[2], CultureInfo.InvariantCulture, out var l15)) cpu.Load15M = l15;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM HostMonitor: failed to read /proc/loadavg.");
        }
    }

    private MemorySample ReadMemory()
    {
        var mem = new MemorySample();
        try
        {
            ulong total = 0, available = 0, swapTotal = 0, swapFree = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if      (TryKb(line, "MemTotal:",     out var v)) total     = v;
                else if (TryKb(line, "MemAvailable:", out v))     available = v;
                else if (TryKb(line, "SwapTotal:",    out v))     swapTotal = v;
                else if (TryKb(line, "SwapFree:",     out v))     swapFree  = v;
            }
            mem.TotalBytes     = total;
            mem.AvailableBytes = available;
            mem.UsedBytes      = total > available ? total - available : 0;
            mem.SwapTotalBytes = swapTotal;
            mem.SwapUsedBytes  = swapTotal > swapFree ? swapTotal - swapFree : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM HostMonitor: failed to read /proc/meminfo.");
        }
        return mem;
    }

    // Parses a "Key:   <number> kB" line into bytes.
    private static bool TryKb(string line, string key, out ulong bytes)
    {
        bytes = 0;
        if (!line.StartsWith(key, StringComparison.Ordinal)) return false;
        var rest = line.AsSpan(key.Length).Trim();
        var space = rest.IndexOf(' ');
        var num = space < 0 ? rest : rest[..space];
        if (ulong.TryParse(num, out var kb)) { bytes = kb * 1024UL; return true; }
        return false;
    }

    private readonly record struct CpuTimes(ulong User, ulong System, ulong Iowait, ulong Idle)
    {
        public ulong Total => User + System + Iowait + Idle;
    }
}
