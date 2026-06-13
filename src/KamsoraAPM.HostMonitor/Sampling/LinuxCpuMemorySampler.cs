// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Host.V1;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// Linux <see cref="ICpuMemorySampler"/> placeholder. Reads /proc/stat for CPU
/// and /proc/meminfo for memory. Full implementation lands in M3.x; for now
/// this stub returns zeros so the daemon still runs on Linux in CI/dev.
/// </summary>
public sealed class LinuxCpuMemorySampler : ICpuMemorySampler
{
    private readonly ILogger<LinuxCpuMemorySampler> _logger;
    private bool _warned;

    public LinuxCpuMemorySampler(ILogger<LinuxCpuMemorySampler> logger)
    {
        _logger = logger;
    }

    public ValueTask<(CpuSample cpu, MemorySample memory)> SampleAsync(CancellationToken cancellationToken)
    {
        if (!_warned)
        {
            _warned = true;
            _logger.LogWarning("KamsoraAPM HostMonitor: Linux sampler is an M3 placeholder; CPU/memory will report as zero until M3.x.");
        }
        return ValueTask.FromResult((new CpuSample
        {
            LogicalCores = (uint)Environment.ProcessorCount,
        }, new MemorySample()));
    }
}
