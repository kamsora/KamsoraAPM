// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Storage.ClickHouse;

/// <summary>
/// Read-side queries against <c>kamsora_apm.host_cpu_memory</c>. Tenant
/// scoping is the first filter on every query (matches partition + order key).
/// </summary>
public sealed class ClickHouseHostReader : IHostReader
{
    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseHostReader> _logger;

    public ClickHouseHostReader(IOptions<ClickHouseOptions> options, ILogger<ClickHouseHostReader> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<IReadOnlyList<HostSummary>> ListHostsAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        // One row per host: latest sample's CPU / memory, plus a beat count so
        // the dashboard can show "62 samples in the last hour".
        const string sql = @"
            SELECT host_id,
                   anyLast(host_name)             AS host_name,
                   anyLast(os_type)               AS os_type,
                   anyLast(os_version)            AS os_version,
                   anyLast(logical_cores)         AS logical_cores,
                   argMax(cpu_util_user, timestamp)     AS cpu_util,
                   argMax(mem_total_bytes, timestamp)   AS mem_total,
                   argMax(mem_used_bytes,  timestamp)   AS mem_used,
                   max(timestamp)                 AS last_seen,
                   count()                        AS samples
              FROM kamsora_apm.host_cpu_memory
             WHERE tenant_id = {t:UUID}
               AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
             GROUP BY host_id
             ORDER BY last_seen DESC";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                 tenantId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')", fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')", toUtc);

        var list = new List<HostSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var hostId   = reader.GetString(0);
            var hostName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var osType   = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var osVer    = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var cores    = (int)ReadLong(reader, 4);
            var cpu      = ReadDouble(reader, 5);
            var memTotal = ReadLong(reader, 6);
            var memUsed  = ReadLong(reader, 7);
            var lastSeen = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc);
            var samples  = ReadLong(reader, 9);
            var memUtil  = memTotal == 0 ? 0d : (double)memUsed / memTotal;

            list.Add(new HostSummary(
                hostId, hostName, osType, osVer, cores,
                cpu, memTotal, memUsed, memUtil, lastSeen, samples));
        }
        return list;
    }

    public async Task<IReadOnlyList<HostUtilizationPoint>> GetUtilizationTimeseriesAsync(
        Guid tenantId, string hostId, DateTime fromUtc, DateTime toUtc,
        int bucketSeconds, CancellationToken ct)
    {
        if (bucketSeconds <= 0) bucketSeconds = 60;

        string sql = $@"
            SELECT
              toDateTime(intDiv(toUnixTimestamp(timestamp), {bucketSeconds}) * {bucketSeconds}, 'UTC') AS bucket,
              avg(cpu_util_user)                AS cpu_avg,
              max(cpu_util_user)                AS cpu_max,
              toInt64(avg(mem_used_bytes))      AS mem_used_avg,
              toInt64(max(mem_used_bytes))      AS mem_used_max,
              anyLast(mem_total_bytes)          AS mem_total
            FROM kamsora_apm.host_cpu_memory
            WHERE tenant_id = {{t:UUID}}
              AND host_id   = {{h:String}}
              AND timestamp BETWEEN {{f:DateTime64(9, 'UTC')}} AND {{tu:DateTime64(9, 'UTC')}}
            GROUP BY bucket
            ORDER BY bucket";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                 tenantId);
        AddParam(cmd, "h",  "String",               hostId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')", fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')", toUtc);

        var list = new List<HostUtilizationPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new HostUtilizationPoint(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                ReadDouble(reader, 1),
                ReadDouble(reader, 2),
                ReadLong(reader, 3),
                ReadLong(reader, 4),
                ReadLong(reader, 5)));
        }
        return list;
    }

    public async Task<IReadOnlyList<HostDiskPoint>> GetDiskTimeseriesAsync(
        Guid tenantId, string hostId, DateTime fromUtc, DateTime toUtc,
        int bucketSeconds, CancellationToken ct)
    {
        if (bucketSeconds <= 0) bucketSeconds = 60;

        string sql = $@"
            SELECT
              toDateTime(intDiv(toUnixTimestamp(timestamp), {bucketSeconds}) * {bucketSeconds}, 'UTC') AS bucket,
              device,
              toInt64(anyLast(total_bytes))         AS total_bytes,
              toInt64(anyLast(used_bytes))          AS used_bytes,
              toInt64(avg(read_bytes_per_sec))      AS rd_b_avg,
              toInt64(avg(write_bytes_per_sec))     AS wr_b_avg,
              toInt64(max(read_bytes_per_sec))      AS rd_b_max,
              toInt64(max(write_bytes_per_sec))     AS wr_b_max,
              toInt64(avg(reads_per_sec))           AS rd_iops,
              toInt64(avg(writes_per_sec))          AS wr_iops
            FROM kamsora_apm.host_disks
            WHERE tenant_id = {{t:UUID}}
              AND host_id   = {{h:String}}
              AND timestamp BETWEEN {{f:DateTime64(9, 'UTC')}} AND {{tu:DateTime64(9, 'UTC')}}
            GROUP BY bucket, device
            ORDER BY bucket, device";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                 tenantId);
        AddParam(cmd, "h",  "String",               hostId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')", fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')", toUtc);

        var list = new List<HostDiskPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new HostDiskPoint(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                reader.GetString(1),
                ReadLong(reader, 2),
                ReadLong(reader, 3),
                ReadLong(reader, 4),
                ReadLong(reader, 5),
                ReadLong(reader, 6),
                ReadLong(reader, 7),
                ReadLong(reader, 8),
                ReadLong(reader, 9)));
        }
        return list;
    }

    public async Task<IReadOnlyList<HostNetworkPoint>> GetNetworkTimeseriesAsync(
        Guid tenantId, string hostId, DateTime fromUtc, DateTime toUtc,
        int bucketSeconds, CancellationToken ct)
    {
        if (bucketSeconds <= 0) bucketSeconds = 60;

        string sql = $@"
            SELECT
              toDateTime(intDiv(toUnixTimestamp(timestamp), {bucketSeconds}) * {bucketSeconds}, 'UTC') AS bucket,
              interface_name,
              toInt64(avg(rx_bytes_per_sec))    AS rx_avg,
              toInt64(avg(tx_bytes_per_sec))    AS tx_avg,
              toInt64(max(rx_bytes_per_sec))    AS rx_max,
              toInt64(max(tx_bytes_per_sec))    AS tx_max,
              toInt64(avg(rx_packets_per_sec))  AS rx_pps,
              toInt64(avg(tx_packets_per_sec))  AS tx_pps
            FROM kamsora_apm.host_networks
            WHERE tenant_id = {{t:UUID}}
              AND host_id   = {{h:String}}
              AND timestamp BETWEEN {{f:DateTime64(9, 'UTC')}} AND {{tu:DateTime64(9, 'UTC')}}
            GROUP BY bucket, interface_name
            ORDER BY bucket, interface_name";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                 tenantId);
        AddParam(cmd, "h",  "String",               hostId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')", fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')", toUtc);

        var list = new List<HostNetworkPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new HostNetworkPoint(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                reader.GetString(1),
                ReadLong(reader, 2),
                ReadLong(reader, 3),
                ReadLong(reader, 4),
                ReadLong(reader, 5),
                ReadLong(reader, 6),
                ReadLong(reader, 7)));
        }
        return list;
    }

    public async Task<IReadOnlyList<HostProcessSummary>> GetTopProcessesAsync(
        Guid tenantId, string hostId, int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 50;

        // Pick the most recent snapshot's processes. argMax over (pid) selects
        // the row at max(timestamp) per pid so a long window doesn't return stale rows.
        const string sql = @"
            WITH latest AS
            (
                SELECT max(timestamp) AS ts
                FROM kamsora_apm.host_processes
                WHERE tenant_id = {t:UUID} AND host_id = {h:String}
            )
            SELECT timestamp,
                   pid, command, service_name, runtime_version,
                   cpu_utilization,
                   toInt64(rss_bytes)   AS rss,
                   toInt32(thread_count) AS th,
                   toInt32(handle_count) AS hd
              FROM kamsora_apm.host_processes
             WHERE tenant_id = {t:UUID}
               AND host_id   = {h:String}
               AND timestamp = (SELECT ts FROM latest)
             ORDER BY cpu_utilization DESC
             LIMIT {l:UInt32}";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t", "UUID",   tenantId);
        AddParam(cmd, "h", "String", hostId);
        AddParam(cmd, "l", "UInt32", (uint)limit);

        var list = new List<HostProcessSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new HostProcessSummary(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                (int)ReadLong(reader, 1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                ReadDouble(reader, 5),
                ReadLong(reader, 6),
                (int)ReadLong(reader, 7),
                (int)ReadLong(reader, 8)));
        }
        return list;
    }

    private static double ReadDouble(System.Data.Common.DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return 0d;
        var v = Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        return double.IsFinite(v) ? v : 0d;
    }

    private static long ReadLong(System.Data.Common.DbDataReader r, int i)
        => r.IsDBNull(i) ? 0L : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, string chType, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value         = value;
        var typeProp = p.GetType().GetProperty("ClickHouseDbType");
        if (typeProp is not null)
        {
            var inner = chType.StartsWith("Nullable(", StringComparison.Ordinal) && chType.EndsWith(')')
                ? chType.Substring("Nullable(".Length, chType.Length - "Nullable(".Length - 1)
                : chType.Split('(')[0];
            if (Enum.TryParse(typeProp.PropertyType, inner, true, out var typeEnum))
            {
                typeProp.SetValue(p, typeEnum);
            }
        }
        cmd.Parameters.Add(p);
    }
}
