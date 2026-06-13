// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Models;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Storage.ClickHouse;

/// <summary>Aggregation queries for the Dashboard.Api over <c>kamsora_apm.spans</c>.</summary>
public sealed class ClickHouseInsightsReader : IInsightsReader
{
    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseInsightsReader> _logger;

    public ClickHouseInsightsReader(IOptions<ClickHouseOptions> options, ILogger<ClickHouseInsightsReader> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<OverviewSnapshot> GetOverviewAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        const string sql = @"
            SELECT
              count()                                                              AS total_spans,
              countIf(status_code = 'ERROR' OR http_status_code >= 500)            AS error_spans,
              quantile(0.5)(duration_ns)  / 1000000                                AS p50_ms,
              quantile(0.9)(duration_ns)  / 1000000                                AS p90_ms,
              quantile(0.99)(duration_ns) / 1000000                                AS p99_ms,
              uniqExact(service_name)                                              AS services
            FROM kamsora_apm.spans
            WHERE tenant_id = {t:UUID}
              AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                  tenantId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')",  fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')",  toUtc);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new OverviewSnapshot(0, 0, 0, 0, 0, 0, 0);
        }

        var total  = ReadLong(reader, 0);
        var errors = ReadLong(reader, 1);
        var p50    = ReadDouble(reader, 2);
        var p90    = ReadDouble(reader, 3);
        var p99    = ReadDouble(reader, 4);
        var svc    = (int)ReadLong(reader, 5);
        var rate   = total == 0 ? 0d : (double)errors / total;
        return new OverviewSnapshot(total, errors, rate, p50, p90, p99, svc);
    }

    public async Task<IReadOnlyList<ServiceSummary>> ListServicesAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        const string sql = @"
            SELECT
              service_name,
              anyLast(service_version)                                             AS version,
              count()                                                              AS span_count,
              countIf(status_code = 'ERROR' OR http_status_code >= 500)            AS error_count,
              quantile(0.5)(duration_ns)  / 1000000                                AS p50_ms,
              quantile(0.9)(duration_ns)  / 1000000                                AS p90_ms,
              quantile(0.99)(duration_ns) / 1000000                                AS p99_ms,
              max(timestamp)                                                       AS last_seen
            FROM kamsora_apm.spans
            WHERE tenant_id = {t:UUID}
              AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
            GROUP BY service_name
            ORDER BY span_count DESC";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                  tenantId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')",  fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')",  toUtc);

        var list = new List<ServiceSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var ver  = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var cnt  = ReadLong(reader, 2);
            var err  = ReadLong(reader, 3);
            var p50  = ReadDouble(reader, 4);
            var p90  = ReadDouble(reader, 5);
            var p99  = ReadDouble(reader, 6);
            var seen = reader.GetDateTime(7);
            var rate = cnt == 0 ? 0d : (double)err / cnt;
            list.Add(new ServiceSummary(name, ver, cnt, err, rate, p50, p90, p99, DateTime.SpecifyKind(seen, DateTimeKind.Utc)));
        }
        return list;
    }

    public async Task<IReadOnlyList<TimeseriesPoint>> GetLatencyTimeseriesAsync(
        Guid tenantId, string? serviceName, DateTime fromUtc, DateTime toUtc, int bucketSeconds, CancellationToken ct)
    {
        if (bucketSeconds <= 0) bucketSeconds = 60;

        string sql = $@"
            SELECT
              toDateTime(intDiv(toUnixTimestamp(timestamp), {bucketSeconds}) * {bucketSeconds}, 'UTC') AS bucket,
              count()                                                              AS span_count,
              countIf(status_code = 'ERROR' OR http_status_code >= 500)            AS error_count,
              quantile(0.5)(duration_ns)  / 1000000                                AS p50_ms,
              quantile(0.9)(duration_ns)  / 1000000                                AS p90_ms,
              quantile(0.99)(duration_ns) / 1000000                                AS p99_ms
            FROM kamsora_apm.spans
            WHERE tenant_id = {{t:UUID}}
              AND timestamp BETWEEN {{f:DateTime64(9, 'UTC')}} AND {{tu:DateTime64(9, 'UTC')}}
              AND ({{svc:Nullable(String)}} IS NULL OR service_name = {{svc:String}})
            GROUP BY bucket
            ORDER BY bucket";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",   "UUID",                  tenantId);
        AddParam(cmd, "f",   "DateTime64(9, 'UTC')",  fromUtc);
        AddParam(cmd, "tu",  "DateTime64(9, 'UTC')",  toUtc);
        AddParam(cmd, "svc", "Nullable(String)",      (object?)serviceName ?? DBNull.Value);

        var list = new List<TimeseriesPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new TimeseriesPoint(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                ReadLong(reader, 1),
                ReadLong(reader, 2),
                ReadDouble(reader, 3),
                ReadDouble(reader, 4),
                ReadDouble(reader, 5)));
        }
        return list;
    }

    public async Task<IReadOnlyList<SpanRow>> GetTraceAsync(Guid tenantId, string traceIdHex, CancellationToken ct)
    {
        const string sql = @"
            SELECT tenant_id, timestamp, trace_id, span_id, parent_span_id, trace_state,
                   service_name, service_namespace, service_version, span_name, span_kind,
                   start_time_unix_ns, end_time_unix_ns, status_code, status_message,
                   http_method, http_status_code, http_route, http_url, http_client_ip,
                   db_system, db_statement, db_duration_ns, attrs_keys, attrs_values,
                   event_names, event_times_unix_ns, event_attrs_json, agent_version
              FROM kamsora_apm.spans
             WHERE tenant_id = {t:UUID}
               AND trace_id  = {tid:String}
             ORDER BY start_time_unix_ns ASC";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",   "UUID",   tenantId);
        AddParam(cmd, "tid", "String", traceIdHex.ToLowerInvariant());

        var rows = new List<SpanRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadSpanRow(reader));
        }
        return rows;
    }

    public async Task<IReadOnlyList<TopRoute>> GetTopRoutesAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 20;

        const string sql = @"
            SELECT service_name, span_name, http_method, http_route,
                   count() AS span_count,
                   countIf(status_code = 'ERROR' OR http_status_code >= 500) AS error_count,
                   quantile(0.5)(duration_ns)  / 1000000 AS p50,
                   quantile(0.9)(duration_ns)  / 1000000 AS p90,
                   quantile(0.99)(duration_ns) / 1000000 AS p99
              FROM kamsora_apm.spans
             WHERE tenant_id = {t:UUID}
               AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
             GROUP BY service_name, span_name, http_method, http_route
             ORDER BY span_count DESC
             LIMIT {l:UInt32}";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                  tenantId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')",  fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')",  toUtc);
        AddParam(cmd, "l",  "UInt32",                (uint)limit);

        var list = new List<TopRoute>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new TopRoute(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                ReadLong(reader, 4),
                ReadLong(reader, 5),
                ReadDouble(reader, 6),
                ReadDouble(reader, 7),
                ReadDouble(reader, 8)));
        }
        return list;
    }

    public async Task<DatabaseOverview> GetDatabaseOverviewAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        const string sql = @"
            SELECT
              count()                                                              AS total_queries,
              countIf(status_code = 'ERROR')                                       AS error_queries,
              quantile(0.5)(duration_ns)  / 1000000                                AS p50_ms,
              quantile(0.9)(duration_ns)  / 1000000                                AS p90_ms,
              quantile(0.99)(duration_ns) / 1000000                                AS p99_ms,
              sum(duration_ns)            / 1000000                                AS total_db_ms,
              uniqExact(db_system)                                                 AS distinct_systems
            FROM kamsora_apm.spans
            WHERE tenant_id = {t:UUID}
              AND db_system != ''
              AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                 tenantId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')", fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')", toUtc);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return new DatabaseOverview(0, 0, 0, 0, 0, 0, 0, 0);

        var total   = ReadLong(reader, 0);
        var errors  = ReadLong(reader, 1);
        var p50     = ReadDouble(reader, 2);
        var p90     = ReadDouble(reader, 3);
        var p99     = ReadDouble(reader, 4);
        var totalMs = ReadDouble(reader, 5);
        var systems = (int)ReadLong(reader, 6);
        var rate    = total == 0 ? 0d : (double)errors / total;
        return new DatabaseOverview(total, errors, rate, p50, p90, p99, totalMs, systems);
    }

    public async Task<IReadOnlyList<TopQuery>> GetTopQueriesAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 20;

        // Group by db_system + first 200 chars of statement so different parameter
        // values for the same query template collapse to one row. M2 will swap this
        // for proper SQL fingerprinting (literal-stripping).
        const string sql = @"
            SELECT db_system,
                   substring(db_statement, 1, 200)                                 AS query_template,
                   count()                                                         AS query_count,
                   countIf(status_code = 'ERROR')                                  AS error_count,
                   quantile(0.5)(duration_ns)  / 1000000                           AS p50_ms,
                   quantile(0.9)(duration_ns)  / 1000000                           AS p90_ms,
                   quantile(0.99)(duration_ns) / 1000000                           AS p99_ms,
                   sum(duration_ns)            / 1000000                           AS total_ms
              FROM kamsora_apm.spans
             WHERE tenant_id = {t:UUID}
               AND db_system != ''
               AND db_statement != ''
               AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
             GROUP BY db_system, query_template
             ORDER BY query_count DESC
             LIMIT {l:UInt32}";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                 tenantId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')", fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')", toUtc);
        AddParam(cmd, "l",  "UInt32",               (uint)limit);

        var list = new List<TopQuery>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new TopQuery(
                reader.GetString(0),
                reader.GetString(1),
                ReadLong(reader, 2),
                ReadLong(reader, 3),
                ReadDouble(reader, 4),
                ReadDouble(reader, 5),
                ReadDouble(reader, 6),
                ReadDouble(reader, 7)));
        }
        return list;
    }

    public async Task<IReadOnlyList<DbSystemBreakdown>> GetDbSystemBreakdownAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        const string sql = @"
            SELECT db_system,
                   count()                                                         AS query_count,
                   quantile(0.5)(duration_ns)  / 1000000                           AS p50_ms,
                   quantile(0.99)(duration_ns) / 1000000                           AS p99_ms
              FROM kamsora_apm.spans
             WHERE tenant_id = {t:UUID}
               AND db_system != ''
               AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
             GROUP BY db_system
             ORDER BY query_count DESC";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                 tenantId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')", fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')", toUtc);

        var list = new List<DbSystemBreakdown>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DbSystemBreakdown(
                reader.GetString(0),
                ReadLong(reader, 1),
                ReadDouble(reader, 2),
                ReadDouble(reader, 3)));
        }
        return list;
    }

    public async Task<IReadOnlyList<EntitySparkline>> GetServiceSparklinesAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, int buckets, CancellationToken ct)
    {
        const string sql = @"
            SELECT
              service_name                                                         AS k,
              toInt32(intDiv(toUnixTimestamp(timestamp) - {f0:Int64}, {w:Int64}))  AS idx,
              count()                                                              AS c,
              countIf(status_code = 'ERROR' OR http_status_code >= 500)            AS e
            FROM kamsora_apm.spans
            WHERE tenant_id = {t:UUID}
              AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
            GROUP BY k, idx";

        return await RunSparklineQueryAsync(sql, tenantId, fromUtc, toUtc, buckets, null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EntitySparkline>> GetConsumerSparklinesAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, int buckets, int limit, CancellationToken ct)
    {
        // SERVER spans only - a consumer is whoever called *us*. Empty consumer
        // ids surface under the same '(anonymous)' key the Consumers list uses.
        const string sql = @"
            SELECT
              if(consumer_id = '', '(anonymous)', consumer_id)                     AS k,
              toInt32(intDiv(toUnixTimestamp(timestamp) - {f0:Int64}, {w:Int64}))  AS idx,
              count()                                                              AS c,
              countIf(status_code = 'ERROR' OR http_status_code >= 400)            AS e
            FROM kamsora_apm.spans
            WHERE tenant_id = {t:UUID}
              AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
              AND span_kind = 'SERVER'
              AND consumer_id IN (
                  SELECT consumer_id
                  FROM kamsora_apm.spans
                  WHERE tenant_id = {t:UUID}
                    AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
                    AND span_kind = 'SERVER'
                  GROUP BY consumer_id
                  ORDER BY count() DESC
                  LIMIT {l:UInt32})
            GROUP BY k, idx";

        return await RunSparklineQueryAsync(sql, tenantId, fromUtc, toUtc, buckets, limit, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<EntitySparkline>> RunSparklineQueryAsync(
        string sql, Guid tenantId, DateTime fromUtc, DateTime toUtc, int buckets, int? limit, CancellationToken ct)
    {
        if (buckets is <= 0 or > 200) buckets = 30;
        var windowSeconds = Math.Max(1, (long)(toUtc - fromUtc).TotalSeconds);
        var bucketSeconds = Math.Max(1, windowSeconds / buckets);
        var fromUnix      = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",                 tenantId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')", fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')", toUtc);
        AddParam(cmd, "f0", "Int64",                fromUnix);
        AddParam(cmd, "w",  "Int64",                bucketSeconds);
        if (limit.HasValue) AddParam(cmd, "l", "UInt32", (uint)Math.Max(1, limit.Value));

        var perKey = new Dictionary<string, (long[] Counts, long[] Errors)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            var idx = (int)ReadLong(reader, 1);
            if (idx < 0) continue;
            // The final partial bucket (timestamp == toUtc edge) folds into the last slot.
            if (idx >= buckets) idx = buckets - 1;
            if (!perKey.TryGetValue(key, out var arrays))
            {
                arrays = (new long[buckets], new long[buckets]);
                perKey[key] = arrays;
            }
            arrays.Counts[idx] += ReadLong(reader, 2);
            arrays.Errors[idx] += ReadLong(reader, 3);
        }

        return perKey
            .Select(kv => new EntitySparkline(kv.Key, kv.Value.Counts, kv.Value.Errors))
            .OrderByDescending(s => s.Counts.Sum())
            .ToList();
    }

    public async Task<RouteDetail?> GetRouteDetailAsync(
        Guid tenantId, string serviceName, string httpRoute,
        DateTime fromUtc, DateTime toUtc, int bucketSeconds, CancellationToken ct)
    {
        if (bucketSeconds <= 0) bucketSeconds = 60;

        const string summarySql = @"
            SELECT
              count()                                                              AS total,
              countIf(status_code = 'ERROR' OR http_status_code >= 500)            AS errors,
              anyHeavy(http_method)                                                AS method,
              quantile(0.5)(duration_ns)  / 1000000                                AS p50_ms,
              quantile(0.75)(duration_ns) / 1000000                                AS p75_ms,
              quantile(0.95)(duration_ns) / 1000000                                AS p95_ms,
              quantile(0.99)(duration_ns) / 1000000                                AS p99_ms
            FROM kamsora_apm.spans
            WHERE tenant_id = {t:UUID}
              AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
              AND service_name = {s:String}
              AND http_route   = {r:String}
              AND span_kind    = 'SERVER'";

        // log2 latency buckets: bucket b covers [2^b, 2^(b+1)) ms, clamped to
        // [-4, 14] → 62µs … 16s+. Exponential buckets stay readable across the
        // whole latency spectrum, unlike fixed-width ones.
        const string histogramSql = @"
            SELECT
              greatest(-4, least(14, toInt32(floor(log2(greatest(duration_ns / 1000000.0, 0.0001)))))) AS b,
              count()                                                                                  AS c
            FROM kamsora_apm.spans
            WHERE tenant_id = {t:UUID}
              AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
              AND service_name = {s:String}
              AND http_route   = {r:String}
              AND span_kind    = 'SERVER'
            GROUP BY b
            ORDER BY b";

        string timeseriesSql = $@"
            SELECT
              toDateTime(intDiv(toUnixTimestamp(timestamp), {bucketSeconds}) * {bucketSeconds}, 'UTC') AS bucket,
              count()                                                              AS span_count,
              countIf(status_code = 'ERROR' OR http_status_code >= 500)            AS error_count,
              quantile(0.5)(duration_ns)  / 1000000                                AS p50_ms,
              quantile(0.9)(duration_ns)  / 1000000                                AS p90_ms,
              quantile(0.99)(duration_ns) / 1000000                                AS p99_ms
            FROM kamsora_apm.spans
            WHERE tenant_id = {{t:UUID}}
              AND timestamp BETWEEN {{f:DateTime64(9, 'UTC')}} AND {{tu:DateTime64(9, 'UTC')}}
              AND service_name = {{s:String}}
              AND http_route   = {{r:String}}
              AND span_kind    = 'SERVER'
            GROUP BY bucket
            ORDER BY bucket";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        long total = 0, errors = 0;
        var method = string.Empty;
        double p50 = 0, p75 = 0, p95 = 0, p99 = 0;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText    = summarySql;
            cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
            AddRouteParams(cmd, tenantId, fromUtc, toUtc, serviceName, httpRoute);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                total  = ReadLong(reader, 0);
                errors = ReadLong(reader, 1);
                method = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                p50    = ReadDouble(reader, 3);
                p75    = ReadDouble(reader, 4);
                p95    = ReadDouble(reader, 5);
                p99    = ReadDouble(reader, 6);
            }
        }

        if (total == 0) return null;

        var timeseries = new List<TimeseriesPoint>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText    = timeseriesSql;
            cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
            AddRouteParams(cmd, tenantId, fromUtc, toUtc, serviceName, httpRoute);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                timeseries.Add(new TimeseriesPoint(
                    DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                    ReadLong(reader, 1),
                    ReadLong(reader, 2),
                    ReadDouble(reader, 3),
                    ReadDouble(reader, 4),
                    ReadDouble(reader, 5)));
            }
        }

        var histogram = new List<HistogramBucket>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText    = histogramSql;
            cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
            AddRouteParams(cmd, tenantId, fromUtc, toUtc, serviceName, httpRoute);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var b = (int)ReadLong(reader, 0);
                histogram.Add(new HistogramBucket(Math.Pow(2, b), Math.Pow(2, b + 1), ReadLong(reader, 1)));
            }
        }

        var minutes = Math.Max(1d / 60d, (toUtc - fromUtc).TotalMinutes);
        return new RouteDetail(
            serviceName, method, httpRoute,
            total, errors, (double)errors / total,
            total / minutes,
            p50, p75, p95, p99,
            timeseries, histogram);
    }

    private static void AddRouteParams(
        System.Data.Common.DbCommand cmd, Guid tenantId, DateTime fromUtc, DateTime toUtc,
        string serviceName, string httpRoute)
    {
        AddParam(cmd, "t",  "UUID",                 tenantId);
        AddParam(cmd, "f",  "DateTime64(9, 'UTC')", fromUtc);
        AddParam(cmd, "tu", "DateTime64(9, 'UTC')", toUtc);
        AddParam(cmd, "s",  "String",               serviceName);
        AddParam(cmd, "r",  "String",               httpRoute);
    }

    private static double ReadDouble(System.Data.Common.DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return 0d;
        var v = Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        // quantile()/avg() over an empty set return NaN; System.Text.Json refuses
        // to serialize NaN/Infinity by default and surfaces as 500. Coerce to 0.
        return double.IsFinite(v) ? v : 0d;
    }

    /// <summary>Read a long that may arrive as UInt64 or Int64 from ClickHouse.</summary>
    private static long ReadLong(System.Data.Common.DbDataReader r, int i)
        => r.IsDBNull(i) ? 0L : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);

    private static SpanRow ReadSpanRow(System.Data.Common.DbDataReader r)
    {
        return new SpanRow
        {
            TenantId          = r.GetGuid(0),
            StartTimeUnixNano = r.IsDBNull(11) ? 0UL : Convert.ToUInt64(r.GetValue(11)),
            EndTimeUnixNano   = r.IsDBNull(12) ? 0UL : Convert.ToUInt64(r.GetValue(12)),
            TraceId           = HexToBytes(r.IsDBNull(2) ? string.Empty : r.GetString(2)),
            SpanId            = HexToBytes(r.IsDBNull(3) ? string.Empty : r.GetString(3)),
            ParentSpanId      = HexToBytes(r.IsDBNull(4) ? string.Empty : r.GetString(4)),
            TraceState        = r.IsDBNull(5) ? string.Empty : r.GetString(5),
            ServiceName       = r.IsDBNull(6) ? string.Empty : r.GetString(6),
            ServiceNamespace  = r.IsDBNull(7) ? string.Empty : r.GetString(7),
            ServiceVersion    = r.IsDBNull(8) ? string.Empty : r.GetString(8),
            SpanName          = r.IsDBNull(9) ? string.Empty : r.GetString(9),
            SpanKind          = r.IsDBNull(10) ? "INTERNAL" : r.GetString(10),
            StatusCode        = r.IsDBNull(13) ? "UNSET" : r.GetString(13),
            StatusMessage     = r.IsDBNull(14) ? string.Empty : r.GetString(14),
            HttpMethod        = r.IsDBNull(15) ? string.Empty : r.GetString(15),
            HttpStatusCode    = r.IsDBNull(16) ? (ushort)0 : Convert.ToUInt16(r.GetValue(16)),
            HttpRoute         = r.IsDBNull(17) ? string.Empty : r.GetString(17),
            HttpUrl           = r.IsDBNull(18) ? string.Empty : r.GetString(18),
            HttpClientIp      = r.IsDBNull(19) ? string.Empty : r.GetString(19),
            DbSystem          = r.IsDBNull(20) ? string.Empty : r.GetString(20),
            DbStatement       = r.IsDBNull(21) ? string.Empty : r.GetString(21),
            DbDurationNs      = r.IsDBNull(22) ? 0UL : Convert.ToUInt64(r.GetValue(22)),
            AttrsKeys         = r.IsDBNull(23) ? Array.Empty<string>() : (string[])r.GetValue(23),
            AttrsValues       = r.IsDBNull(24) ? Array.Empty<string>() : (string[])r.GetValue(24),
            EventNames        = r.IsDBNull(25) ? Array.Empty<string>() : (string[])r.GetValue(25),
            EventTimesUnixNs  = r.IsDBNull(26) ? Array.Empty<ulong>()  : ((ulong[])r.GetValue(26)),
            EventAttrsJson    = r.IsDBNull(27) ? Array.Empty<string>() : (string[])r.GetValue(27),
            AgentVersion      = r.IsDBNull(28) ? string.Empty : r.GetString(28),
        };
    }

    private static byte[] HexToBytes(string hex) =>
        string.IsNullOrEmpty(hex) ? Array.Empty<byte>() : Convert.FromHexString(hex);

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
