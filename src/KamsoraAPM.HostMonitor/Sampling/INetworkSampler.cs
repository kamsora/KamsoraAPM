// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Host.V1;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>Per-NIC RX/TX throughput + error counters. One <see cref="NetworkSample"/> per active interface.</summary>
public interface INetworkSampler
{
    ValueTask<IReadOnlyList<NetworkSample>> SampleAsync(CancellationToken cancellationToken);
}
