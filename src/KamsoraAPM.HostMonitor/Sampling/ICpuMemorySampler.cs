// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Host.V1;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// One snapshot of host CPU + memory utilization. Concrete implementations
/// live per-OS (<see cref="WindowsCpuMemorySampler"/>, Linux to follow in M3.x).
/// </summary>
public interface ICpuMemorySampler
{
    /// <summary>Capture one sample. Implementations must be safe to call once per CpuMemoryInterval tick.</summary>
    ValueTask<(CpuSample cpu, MemorySample memory)> SampleAsync(CancellationToken cancellationToken);
}
