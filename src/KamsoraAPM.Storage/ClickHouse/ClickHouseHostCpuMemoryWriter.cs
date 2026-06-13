// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Models;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Storage.ClickHouse;

/// <summary>
/// Bulk inserts host telemetry batches into <c>kamsora_apm.host_*</c> tables via
/// the native bulk-copy path. Implements every <see cref="IHostSnapshotWriter"/>
/// method on a single class so all four host tables share a connection pool and
/// the same retry/observability conventions.
/// </summary>
public sealed class ClickHouseHostCpuMemoryWriter : IHostSnapshotWriter
{
    private const string CpuMemoryTable = "kamsora_apm.host_cpu_memory";
    private const string DisksTable     = "kamsora_apm.host_disks";
    private const string NetworksTable  = "kamsora_apm.host_networks";
    private const string ProcessesTable = "kamsora_apm.host_processes";

    private static readonly string[] CpuMemoryColumns =
    {
        "tenant_id", "timestamp", "host_id", "host_name", "os_type", "os_version",
        "logical_cores", "load_1m", "load_5m", "load_15m",
        "cpu_util_user", "cpu_util_system", "cpu_util_iowait", "cpu_util_idle",
        "mem_total_bytes", "mem_available_bytes", "mem_used_bytes",
        "swap_total_bytes", "swap_used_bytes",
    };

    private static readonly string[] DisksColumns =
    {
        "tenant_id", "timestamp", "host_id", "device", "mountpoint",
        "total_bytes", "used_bytes",
        "reads_per_sec", "writes_per_sec", "read_bytes_per_sec", "write_bytes_per_sec",
    };

    private static readonly string[] NetworksColumns =
    {
        "tenant_id", "timestamp", "host_id", "interface_name",
        "rx_bytes_per_sec", "tx_bytes_per_sec",
        "rx_packets_per_sec", "tx_packets_per_sec",
        "rx_errors", "tx_errors",
    };

    private static readonly string[] ProcessesColumns =
    {
        "tenant_id", "timestamp", "host_id", "pid", "command", "user_name",
        "runtime_version", "service_name",
        "cpu_utilization", "rss_bytes", "thread_count", "handle_count",
    };

    private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseHostCpuMemoryWriter> _logger;

    public ClickHouseHostCpuMemoryWriter(IOptions<ClickHouseOptions> options, ILogger<ClickHouseHostCpuMemoryWriter> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public Task WriteCpuMemoryAsync(IReadOnlyList<HostCpuMemoryRow> rows, CancellationToken cancellationToken) =>
        BulkInsertAsync(CpuMemoryTable, CpuMemoryColumns, rows, ToCpuMemoryArrays, cancellationToken);

    public Task WriteDisksAsync(IReadOnlyList<HostDiskRow> rows, CancellationToken cancellationToken) =>
        BulkInsertAsync(DisksTable, DisksColumns, rows, ToDiskArrays, cancellationToken);

    public Task WriteNetworksAsync(IReadOnlyList<HostNetworkRow> rows, CancellationToken cancellationToken) =>
        BulkInsertAsync(NetworksTable, NetworksColumns, rows, ToNetworkArrays, cancellationToken);

    public Task WriteProcessesAsync(IReadOnlyList<HostProcessRow> rows, CancellationToken cancellationToken) =>
        BulkInsertAsync(ProcessesTable, ProcessesColumns, rows, ToProcessArrays, cancellationToken);

    private async Task BulkInsertAsync<T>(
        string table,
        string[] columns,
        IReadOnlyList<T> rows,
        Func<IReadOnlyList<T>, IEnumerable<object?[]>> projector,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return;

        await using var connection = new ClickHouseConnection(_options.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using var bulkCopy = new ClickHouseBulkCopy(connection)
        {
            DestinationTableName   = table,
            ColumnNames            = columns,
            BatchSize              = rows.Count,
            MaxDegreeOfParallelism = 1,
        };

        await bulkCopy.InitAsync().ConfigureAwait(false);
        await bulkCopy.WriteToServerAsync(projector(rows), ct).ConfigureAwait(false);

        _logger.LogDebug("ClickHouse: inserted {Count} row(s) into {Table}.", rows.Count, table);
    }

    private static DateTime FromUnixNanos(ulong unixNanos) =>
        UnixEpochUtc.AddTicks((long)(unixNanos / 100UL));

    private static IEnumerable<object?[]> ToCpuMemoryArrays(IReadOnlyList<HostCpuMemoryRow> rows)
    {
        foreach (var r in rows)
        {
            yield return new object?[]
            {
                r.TenantId, FromUnixNanos(r.TimeUnixNano),
                r.HostId, r.HostName, r.OsType, r.OsVersion,
                r.LogicalCores,
                r.Load1m, r.Load5m, r.Load15m,
                r.CpuUtilUser, r.CpuUtilSystem, r.CpuUtilIowait, r.CpuUtilIdle,
                r.MemTotalBytes, r.MemAvailableBytes, r.MemUsedBytes,
                r.SwapTotalBytes, r.SwapUsedBytes,
            };
        }
    }

    private static IEnumerable<object?[]> ToDiskArrays(IReadOnlyList<HostDiskRow> rows)
    {
        foreach (var r in rows)
        {
            yield return new object?[]
            {
                r.TenantId, FromUnixNanos(r.TimeUnixNano),
                r.HostId, r.Device, r.Mountpoint,
                r.TotalBytes, r.UsedBytes,
                r.ReadsPerSec, r.WritesPerSec,
                r.ReadBytesPerSec, r.WriteBytesPerSec,
            };
        }
    }

    private static IEnumerable<object?[]> ToNetworkArrays(IReadOnlyList<HostNetworkRow> rows)
    {
        foreach (var r in rows)
        {
            yield return new object?[]
            {
                r.TenantId, FromUnixNanos(r.TimeUnixNano),
                r.HostId, r.InterfaceName,
                r.RxBytesPerSec, r.TxBytesPerSec,
                r.RxPacketsPerSec, r.TxPacketsPerSec,
                r.RxErrors, r.TxErrors,
            };
        }
    }

    private static IEnumerable<object?[]> ToProcessArrays(IReadOnlyList<HostProcessRow> rows)
    {
        foreach (var r in rows)
        {
            yield return new object?[]
            {
                r.TenantId, FromUnixNanos(r.TimeUnixNano),
                r.HostId, r.Pid, r.Command, r.UserName,
                r.RuntimeVersion, r.ServiceName,
                r.CpuUtilization, r.RssBytes, r.ThreadCount, r.HandleCount,
            };
        }
    }
}
