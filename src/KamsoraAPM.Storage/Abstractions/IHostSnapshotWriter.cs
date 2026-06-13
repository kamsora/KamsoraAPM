// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>
/// Persists batches of host CPU/memory snapshots. Follows the same contract as
/// <see cref="ISpanWriter"/>: concurrent-safe, throw on persistent failure.
/// </summary>
public interface IHostSnapshotWriter
{
    Task WriteCpuMemoryAsync(IReadOnlyList<HostCpuMemoryRow> rows, CancellationToken cancellationToken);
    Task WriteDisksAsync(IReadOnlyList<HostDiskRow> rows, CancellationToken cancellationToken);
    Task WriteNetworksAsync(IReadOnlyList<HostNetworkRow> rows, CancellationToken cancellationToken);
    Task WriteProcessesAsync(IReadOnlyList<HostProcessRow> rows, CancellationToken cancellationToken);
}
