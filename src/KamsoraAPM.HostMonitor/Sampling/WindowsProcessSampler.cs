// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Runtime.Versioning;
using KamsoraAPM.Contracts.Host.V1;
using KamsoraAPM.HostMonitor.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// Windows process sampler. Computes per-process CPU utilization as a delta of
/// <see cref="Process.TotalProcessorTime"/> between consecutive samples,
/// normalized by elapsed wall-clock time and logical-core count. Returns the
/// top-N by CPU (configurable via <see cref="HostMonitorOptions.TopProcesses"/>).
///
/// Limitations of the M3.x cut:
///   - Per-process user / runtime-version / service-name lookups require WMI or
///     cross-process module inspection (elevated). Left empty here.
///   - Processes that exit between samples are silently dropped (no last-known).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessSampler : IProcessSampler
{
    private readonly HostMonitorOptions _options;
    private readonly ILogger<WindowsProcessSampler> _logger;
    private readonly Dictionary<int, ProcessSnapshot> _previous = new();
    private readonly int _logicalCores = Environment.ProcessorCount;
    private DateTime _lastSampleUtc = DateTime.UtcNow;

    public WindowsProcessSampler(IOptions<HostMonitorOptions> options, ILogger<WindowsProcessSampler> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public ValueTask<IReadOnlyList<ProcessSample>> SampleAsync(CancellationToken cancellationToken)
    {
        var nowUtc       = DateTime.UtcNow;
        var elapsedSec   = Math.Max(0.001, (nowUtc - _lastSampleUtc).TotalSeconds);
        var nextPrevious = new Dictionary<int, ProcessSnapshot>(_previous.Count);
        var rows         = new List<ProcessSample>(128);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var pid       = p.Id;
                var totalCpu  = p.TotalProcessorTime;
                var current   = new ProcessSnapshot(totalCpu, nowUtc);
                nextPrevious[pid] = current;

                double cpuUtil = 0d;
                if (_previous.TryGetValue(pid, out var prev))
                {
                    var cpuDeltaSec = (totalCpu - prev.TotalCpu).TotalSeconds;
                    if (cpuDeltaSec > 0)
                    {
                        cpuUtil = Math.Clamp(cpuDeltaSec / elapsedSec / _logicalCores, 0d, 1d);
                    }
                }

                rows.Add(new ProcessSample
                {
                    Pid            = (uint)pid,
                    Command        = p.ProcessName,
                    User           = string.Empty,
                    RuntimeVersion = string.Empty,
                    ServiceName    = string.Empty,
                    CpuUtilization = cpuUtil,
                    RssBytes       = (ulong)p.WorkingSet64,
                    ThreadCount    = (uint)p.Threads.Count,
                    HandleCount    = (uint)p.HandleCount,
                });
            }
            catch
            {
                // Process exited between enumeration and read, or access denied (System / Idle).
            }
            finally
            {
                p.Dispose();
            }
        }

        _previous.Clear();
        foreach (var kv in nextPrevious) _previous[kv.Key] = kv.Value;
        _lastSampleUtc = nowUtc;

        // Sort by CPU descending and trim to TopProcesses. Skip the first sample
        // (no prior baseline → all zeros); the next tick produces real data.
        rows.Sort((a, b) => b.CpuUtilization.CompareTo(a.CpuUtilization));
        if (rows.Count > _options.TopProcesses)
            rows = rows.GetRange(0, _options.TopProcesses);

        return ValueTask.FromResult<IReadOnlyList<ProcessSample>>(rows);
    }

    private readonly record struct ProcessSnapshot(TimeSpan TotalCpu, DateTime SampledAtUtc);
}
