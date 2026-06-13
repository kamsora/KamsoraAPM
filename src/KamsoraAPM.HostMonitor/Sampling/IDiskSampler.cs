// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Host.V1;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>Per-volume disk capacity + I/O throughput. One <see cref="DiskSample"/> per fixed drive.</summary>
public interface IDiskSampler
{
    ValueTask<IReadOnlyList<DiskSample>> SampleAsync(CancellationToken cancellationToken);
}
