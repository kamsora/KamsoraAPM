// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Models;

/// <summary>
/// Storage-shaped row for <c>kamsora_apm.host_cpu_memory</c>. Populated by the
/// Collector from a <see cref="KamsoraAPM.Contracts.Host.V1.HostSnapshot"/>
/// (one row per snapshot). M3 vertical-MVP captures CPU + memory only; disks,
/// networks, and processes follow in M3.x.
/// </summary>
public sealed class HostCpuMemoryRow
{
    public Guid   TenantId            { get; set; }
    /// <summary>Snapshot time in nanoseconds since unix epoch.</summary>
    public ulong  TimeUnixNano        { get; set; }

    public string HostId              { get; set; } = string.Empty;
    public string HostName            { get; set; } = string.Empty;
    public string OsType              { get; set; } = string.Empty;
    public string OsVersion           { get; set; } = string.Empty;

    public ushort LogicalCores        { get; set; }
    public float  Load1m              { get; set; }
    public float  Load5m              { get; set; }
    public float  Load15m             { get; set; }
    public float  CpuUtilUser         { get; set; }
    public float  CpuUtilSystem       { get; set; }
    public float  CpuUtilIowait       { get; set; }
    public float  CpuUtilIdle         { get; set; }

    public ulong  MemTotalBytes       { get; set; }
    public ulong  MemAvailableBytes   { get; set; }
    public ulong  MemUsedBytes        { get; set; }
    public ulong  SwapTotalBytes      { get; set; }
    public ulong  SwapUsedBytes       { get; set; }
}
