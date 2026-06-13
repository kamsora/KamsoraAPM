// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Host.V1;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>No-op disk sampler used on platforms where the native sampler is not yet implemented.</summary>
internal sealed class NullDiskSampler : IDiskSampler
{
    public ValueTask<IReadOnlyList<DiskSample>> SampleAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<DiskSample>>(Array.Empty<DiskSample>());
}

/// <summary>No-op network sampler used on platforms where the native sampler is not yet implemented.</summary>
internal sealed class NullNetworkSampler : INetworkSampler
{
    public ValueTask<IReadOnlyList<NetworkSample>> SampleAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<NetworkSample>>(Array.Empty<NetworkSample>());
}

/// <summary>No-op process sampler used on platforms where the native sampler is not yet implemented.</summary>
internal sealed class NullProcessSampler : IProcessSampler
{
    public ValueTask<IReadOnlyList<ProcessSample>> SampleAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<ProcessSample>>(Array.Empty<ProcessSample>());
}
