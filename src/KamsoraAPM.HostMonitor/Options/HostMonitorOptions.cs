// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel.DataAnnotations;

namespace KamsoraAPM.HostMonitor.Options;

/// <summary>Runtime knobs for the HostMonitor daemon.</summary>
public sealed class HostMonitorOptions
{
    /// <summary>gRPC endpoint of the KamsoraAPM Collector (e.g. <c>http://localhost:5080</c>).</summary>
    [Required]
    public string CollectorEndpoint { get; set; } = "http://localhost:5080";

    /// <summary>Tenant UUID this host belongs to. Issued from the dashboard's "Hosts" page.</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Tenant API key. Same secret format the Agent uses.</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Friendly host label override. When empty, defaults to <see cref="Environment.MachineName"/>.</summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>Stable host identifier override. When empty, derived from the OS machine GUID.</summary>
    public string HostIdOverride { get; set; } = string.Empty;

    /// <summary>Sampling cadence for CPU + memory snapshots. Default 10 s.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan CpuMemoryInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum snapshots buffered per gRPC export call. Default 6 (one minute at 10 s cadence).</summary>
    [Range(1, 1000)]
    public int MaxBatchSize { get; set; } = 6;

    /// <summary>Optional service name carried in self-logs.</summary>
    public string SelfServiceName { get; set; } = "kamsora-apm-host-monitor";

    /// <summary>Number of top-CPU processes captured per snapshot. Caps the per-snapshot payload. Default 50.</summary>
    [Range(0, 500)]
    public int TopProcesses { get; set; } = 50;

}
