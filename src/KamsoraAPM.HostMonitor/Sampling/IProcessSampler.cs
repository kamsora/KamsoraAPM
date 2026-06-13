// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Host.V1;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>Top-N hottest processes by CPU. Bounded by <see cref="Options.HostMonitorOptions.TopProcesses"/>.</summary>
public interface IProcessSampler
{
    ValueTask<IReadOnlyList<ProcessSample>> SampleAsync(CancellationToken cancellationToken);
}
