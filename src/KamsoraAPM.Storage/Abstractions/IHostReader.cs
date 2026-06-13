// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>
/// Read-side queries for host telemetry, scoped per tenant. Powers the
/// Dashboard.Api Hosts endpoints.
/// </summary>
public interface IHostReader
{
    /// <summary>One row per host that has reported within the window, with its most recent sample.</summary>
    Task<IReadOnlyList<HostSummary>> ListHostsAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>CPU + memory utilization timeseries for a single host. Bucketed by <paramref name="bucketSeconds"/>.</summary>
    Task<IReadOnlyList<HostUtilizationPoint>> GetUtilizationTimeseriesAsync(
        Guid tenantId, string hostId, DateTime fromUtc, DateTime toUtc,
        int bucketSeconds, CancellationToken ct);

    /// <summary>Per-volume disk I/O timeseries. Each row is (bucket, device, capacity + avg/max I/O rates).</summary>
    Task<IReadOnlyList<HostDiskPoint>> GetDiskTimeseriesAsync(
        Guid tenantId, string hostId, DateTime fromUtc, DateTime toUtc,
        int bucketSeconds, CancellationToken ct);

    /// <summary>Per-NIC throughput timeseries. Excludes loopback/tunneling (already filtered at the sampler).</summary>
    Task<IReadOnlyList<HostNetworkPoint>> GetNetworkTimeseriesAsync(
        Guid tenantId, string hostId, DateTime fromUtc, DateTime toUtc,
        int bucketSeconds, CancellationToken ct);

    /// <summary>Latest snapshot's top-N processes by CPU for one host.</summary>
    Task<IReadOnlyList<HostProcessSummary>> GetTopProcessesAsync(
        Guid tenantId, string hostId, int limit, CancellationToken ct);
}

public sealed record HostSummary(
    string   HostId,
    string   HostName,
    string   OsType,
    string   OsVersion,
    int      LogicalCores,
    double   CpuUtilization,       // 0..1, latest sample
    long     MemTotalBytes,
    long     MemUsedBytes,
    double   MemUtilization,       // 0..1, latest sample
    DateTime LastSeenUtc,
    long     SampleCount);

public sealed record HostUtilizationPoint(
    DateTime BucketStartUtc,
    double   CpuUserAvg,           // 0..1
    double   CpuUserMax,           // 0..1
    long     MemUsedBytesAvg,
    long     MemUsedBytesMax,
    long     MemTotalBytes);       // anyLast within bucket - host RAM doesn't change mid-window

public sealed record HostDiskPoint(
    DateTime BucketStartUtc,
    string   Device,
    long     TotalBytes,
    long     UsedBytes,
    long     ReadBytesPerSecAvg,
    long     WriteBytesPerSecAvg,
    long     ReadBytesPerSecMax,
    long     WriteBytesPerSecMax,
    long     ReadsPerSecAvg,
    long     WritesPerSecAvg);

public sealed record HostNetworkPoint(
    DateTime BucketStartUtc,
    string   InterfaceName,
    long     RxBytesPerSecAvg,
    long     TxBytesPerSecAvg,
    long     RxBytesPerSecMax,
    long     TxBytesPerSecMax,
    long     RxPacketsPerSecAvg,
    long     TxPacketsPerSecAvg);

public sealed record HostProcessSummary(
    DateTime LatestSampleUtc,
    int      Pid,
    string   Command,
    string   ServiceName,
    string   RuntimeVersion,
    double   CpuUtilization,        // 0..1, latest sample
    long     RssBytes,
    int      ThreadCount,
    int      HandleCount);
