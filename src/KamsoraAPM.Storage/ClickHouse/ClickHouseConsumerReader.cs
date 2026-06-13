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
/// Read-side queries for the M6 <c>consumer_hourly_rollup</c> +
/// <c>status_hourly_rollup</c> AggregatingMergeTree tables. Uses
/// <c>*Merge</c> functions to fold the partial aggregate states into
/// real numbers — the materialised view does the heavy aggregation on
/// the write path, so these queries scan ~1 row/(consumer × hour) and
/// stay fast even for tenants with millions of raw spans.
/// </summary>
public sealed class ClickHouseConsumerReader : IConsumerReader, IErrorsReader
{
    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseConsumerReader> _logger;

    public ClickHouseConsumerReader(IOptions<ClickHouseOptions> options, ILogger<ClickHouseConsumerReader> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    // ---- IConsumerReader -------------------------------------------------

    public async Task<IReadOnlyList<ConsumerSummary>> ListConsumersAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc,
        string? serviceName, int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 100;

        const string sql = @"
            SELECT
                consumer_id,
                sumMerge(request_count)                                   AS total_req,
                sumMerge(error_count)                                     AS total_err,
                sumMerge(client_error_count)                              AS total_4xx,
                sumMerge(server_error_count)                              AS total_5xx,
                quantilesTDigestMerge(0.5, 0.9, 0.99)(latency_quantiles)  AS quants,
                uniqExact(http_route)                                     AS distinct_routes,
                min(bucket_hour)                                          AS first_seen,
                max(bucket_hour)                                          AS last_seen
              FROM kamsora_apm.consumer_hourly_rollup
             WHERE tenant_id = {t:UUID}
               AND bucket_hour BETWEEN {f:DateTime} AND {tu:DateTime}
               AND ({svc:Nullable(String)} IS NULL OR service_name = {svc:String})
             GROUP BY consumer_id
             ORDER BY total_req DESC
             LIMIT {l:UInt32}";

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",   "UUID",             tenantId);
        AddParam(cmd, "f",   "DateTime",         fromUtc);
        AddParam(cmd, "tu",  "DateTime",         toUtc);
        AddParam(cmd, "svc", "Nullable(String)", (object?)serviceName ?? DBNull.Value);
        AddParam(cmd, "l",   "UInt32",           (uint)limit);

        var list = new List<ConsumerSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var consumerId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var total      = ReadLong(reader, 1);
            var errors     = ReadLong(reader, 2);
            var s4xx       = ReadLong(reader, 3);
            var s5xx       = ReadLong(reader, 4);
            var (p50, p90, p99) = ReadQuantiles(reader, 5);
            var routes     = (int)ReadLong(reader, 6);
            var first      = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc);
            var last       = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc);
            var rate       = total == 0 ? 0d : (double)errors / total;

            list.Add(new ConsumerSummary(
                ConsumerId:        string.IsNullOrEmpty(consumerId) ? "(anonymous)" : consumerId,
                RequestCount:      total,
                ErrorCount:        errors,
                ErrorRate:         rate,
                ClientErrorCount:  s4xx,
                ServerErrorCount:  s5xx,
                LatencyP50Ms:      p50,
                LatencyP90Ms:      p90,
                LatencyP99Ms:      p99,
                DistinctRoutes:    routes,
                FirstSeenUtc:      first,
                LastSeenUtc:       last));
        }
        return list;
    }

    public async Task<IReadOnlyList<ConsumerTimeseriesPoint>> GetConsumerTimeseriesAsync(
        Guid tenantId, string consumerId, DateTime fromUtc, DateTime toUtc,
        int bucketSeconds, CancellationToken ct)
    {
        if (bucketSeconds < 3600) bucketSeconds = 3600;   // rollup is hourly; smaller buckets meaningless

        string sql = $@"
            SELECT
                toDateTime(intDiv(toUnixTimestamp(bucket_hour), {bucketSeconds}) * {bucketSeconds}, 'UTC') AS bucket,
                sumMerge(request_count)                                  AS total_req,
                sumMerge(error_count)                                    AS total_err,
                sumMerge(client_error_count)                             AS total_4xx,
                sumMerge(server_error_count)                             AS total_5xx,
                quantilesTDigestMerge(0.5, 0.9, 0.99)(latency_quantiles) AS quants
              FROM kamsora_apm.consumer_hourly_rollup
             WHERE tenant_id = {{t:UUID}}
               AND consumer_id = {{c:String}}
               AND bucket_hour BETWEEN {{f:DateTime}} AND {{tu:DateTime}}
             GROUP BY bucket
             ORDER BY bucket";

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",     tenantId);
        AddParam(cmd, "c",  "String",   consumerId == "(anonymous)" ? string.Empty : consumerId);
        AddParam(cmd, "f",  "DateTime", fromUtc);
        AddParam(cmd, "tu", "DateTime", toUtc);

        var list = new List<ConsumerTimeseriesPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var (p50, p90, p99) = ReadQuantiles(reader, 5);
            list.Add(new ConsumerTimeseriesPoint(
                BucketStartUtc:    DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                RequestCount:      ReadLong(reader, 1),
                ErrorCount:        ReadLong(reader, 2),
                ClientErrorCount:  ReadLong(reader, 3),
                ServerErrorCount:  ReadLong(reader, 4),
                LatencyP50Ms:      p50,
                LatencyP90Ms:      p90,
                LatencyP99Ms:      p99));
        }
        return list;
    }

    public async Task<IReadOnlyList<ConsumerRouteSummary>> GetConsumerTopRoutesAsync(
        Guid tenantId, string consumerId, DateTime fromUtc, DateTime toUtc,
        int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 20;

        const string sql = @"
            SELECT
                service_name,
                http_route,
                sumMerge(request_count)                                  AS total_req,
                sumMerge(error_count)                                    AS total_err,
                quantilesTDigestMerge(0.5, 0.9, 0.99)(latency_quantiles) AS quants
              FROM kamsora_apm.consumer_hourly_rollup
             WHERE tenant_id = {t:UUID}
               AND consumer_id = {c:String}
               AND bucket_hour BETWEEN {f:DateTime} AND {tu:DateTime}
             GROUP BY service_name, http_route
             ORDER BY total_req DESC
             LIMIT {l:UInt32}";

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",     tenantId);
        AddParam(cmd, "c",  "String",   consumerId == "(anonymous)" ? string.Empty : consumerId);
        AddParam(cmd, "f",  "DateTime", fromUtc);
        AddParam(cmd, "tu", "DateTime", toUtc);
        AddParam(cmd, "l",  "UInt32",   (uint)limit);

        var list = new List<ConsumerRouteSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var service = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var route   = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var total   = ReadLong(reader, 2);
            var errors  = ReadLong(reader, 3);
            var (p50, p90, p99) = ReadQuantiles(reader, 4);
            var rate = total == 0 ? 0d : (double)errors / total;
            list.Add(new ConsumerRouteSummary(service, route, total, errors, rate, p50, p90, p99));
        }
        return list;
    }

    public async Task<IReadOnlyList<ConsumerRouteWithSparkline>> GetConsumerRoutesWithSparklineAsync(
        Guid tenantId, string consumerId, DateTime fromUtc, DateTime toUtc,
        int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 20;

        // Build the per-hour sparkline by grouping the rollup buckets that fall
        // inside the window, then sorting by bucket and emitting just the
        // request counts. arrayMap pads missing hours with 0 so every row has
        // the same array length — the dashboard renders fixed-width SVGs.
        const string sql = @"
            WITH per_route_per_hour AS (
                SELECT
                    service_name,
                    http_route,
                    bucket_hour,
                    sumMerge(request_count)                                  AS req,
                    sumMerge(error_count)                                    AS err,
                    quantilesTDigestMerge(0.5, 0.99)(latency_quantiles)      AS quants
                  FROM kamsora_apm.consumer_hourly_rollup
                 WHERE tenant_id   = {t:UUID}
                   AND consumer_id = {c:String}
                   AND bucket_hour BETWEEN {f:DateTime} AND {tu:DateTime}
                 GROUP BY service_name, http_route, bucket_hour
            )
            SELECT
                service_name,
                http_route,
                sum(req)                                              AS total_req,
                sum(err)                                              AS total_err,
                quantilesTDigestMerge(0.5, 0.99)(
                    quantilesTDigestStateMerge(arrayMap(x -> x, quants))) AS qq,
                groupArray((toUnixTimestamp(bucket_hour), req))       AS sparks
              FROM per_route_per_hour
             GROUP BY service_name, http_route
             ORDER BY total_req DESC
             LIMIT {l:UInt32}";

        // The above WITH-CTE approach uses quantiles too aggressively. Simplify:
        // do route-totals separately and hourly request counts via a second
        // grouping done inline in one query.
        const string sqlSimple = @"
            SELECT
                service_name,
                http_route,
                sumMerge(request_count)                                  AS total_req,
                sumMerge(error_count)                                    AS total_err,
                quantilesTDigestMerge(0.5, 0.99)(latency_quantiles)      AS quants,
                groupArray((toUnixTimestamp(bucket_hour), sumMerge(request_count))) AS spark_unused
              FROM kamsora_apm.consumer_hourly_rollup
             WHERE tenant_id   = {t:UUID}
               AND consumer_id = {c:String}
               AND bucket_hour BETWEEN {f:DateTime} AND {tu:DateTime}
             GROUP BY service_name, http_route
             ORDER BY total_req DESC
             LIMIT {l:UInt32}";

        // The cleanest path: aggregate twice and join in C#. Cheaper than fancy SQL.
        var routes = await GetConsumerTopRoutesAsync(tenantId, consumerId, fromUtc, toUtc, limit, ct).ConfigureAwait(false);
        if (routes.Count == 0) return Array.Empty<ConsumerRouteWithSparkline>();

        _ = sql; _ = sqlSimple; // documented for posterity

        // Fixed bucket count across the window — same for every row.
        var bucketCount = Math.Max(1, (int)Math.Ceiling((toUtc - fromUtc).TotalHours));
        var windowStart = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc);

        // Pull request counts grouped by (service, route, hour) for the top routes
        // we already identified. We send the routes back as an Array(String) so
        // ClickHouse filters server-side; cheaper than N round-trips.
        var routeKeys = routes.Select(r => $"{r.ServiceName}{r.HttpRoute}").ToArray();

        const string hourlySql = @"
            SELECT
                concat(service_name, '\x01', http_route)                AS k,
                toUnixTimestamp(bucket_hour)                            AS hr,
                sumMerge(request_count)                                 AS req
              FROM kamsora_apm.consumer_hourly_rollup
             WHERE tenant_id = {t:UUID}
               AND consumer_id = {c:String}
               AND bucket_hour BETWEEN {f:DateTime} AND {tu:DateTime}
               AND has({keys:Array(String)}, concat(service_name, '\x01', http_route))
             GROUP BY service_name, http_route, bucket_hour";

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText    = hourlySql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",    "UUID",          tenantId);
        AddParam(cmd, "c",    "String",        consumerId == "(anonymous)" ? string.Empty : consumerId);
        AddParam(cmd, "f",    "DateTime",      fromUtc);
        AddParam(cmd, "tu",   "DateTime",      toUtc);
        AddParam(cmd, "keys", "Array(String)", routeKeys);

        // routeKey -> bucket index -> count
        var perRoute = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var k in routeKeys) perRoute[k] = new long[bucketCount];

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key   = reader.GetString(0);
            var unix  = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            var count = ReadLong(reader, 2);
            var bucketUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            var idx = (int)Math.Floor((bucketUtc - windowStart).TotalHours);
            if (idx >= 0 && idx < bucketCount && perRoute.TryGetValue(key, out var arr))
            {
                arr[idx] = count;
            }
        }

        return routes.Select(r =>
        {
            var key = $"{r.ServiceName}{r.HttpRoute}";
            var spark = perRoute.TryGetValue(key, out var arr) ? arr : new long[bucketCount];
            return new ConsumerRouteWithSparkline(
                r.ServiceName, r.HttpRoute, r.RequestCount, r.ErrorCount, r.ErrorRate,
                r.LatencyP50Ms, r.LatencyP99Ms, spark);
        }).ToArray();
    }

    // ---- IErrorsReader ---------------------------------------------------

    public async Task<IReadOnlyList<RouteStatusSummary>> GetRouteStatusBreakdownAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc,
        string? serviceName, int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 50;

        const string sql = @"
            SELECT
                service_name,
                http_route,
                sumMerge(request_count)                                              AS total_req,
                sumMergeIf(request_count, http_status_code BETWEEN 200 AND 299)      AS s2xx,
                sumMergeIf(request_count, http_status_code BETWEEN 300 AND 399)      AS s3xx,
                sumMergeIf(request_count, http_status_code BETWEEN 400 AND 499)      AS s4xx,
                sumMergeIf(request_count, http_status_code >= 500)                   AS s5xx,
                quantilesTDigestMerge(0.5, 0.99)(latency_quantiles)                  AS quants
              FROM kamsora_apm.status_hourly_rollup
             WHERE tenant_id = {t:UUID}
               AND bucket_hour BETWEEN {f:DateTime} AND {tu:DateTime}
               AND ({svc:Nullable(String)} IS NULL OR service_name = {svc:String})
             GROUP BY service_name, http_route
             HAVING total_req > 0
             ORDER BY (s4xx + s5xx) DESC, total_req DESC
             LIMIT {l:UInt32}";

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",   "UUID",             tenantId);
        AddParam(cmd, "f",   "DateTime",         fromUtc);
        AddParam(cmd, "tu",  "DateTime",         toUtc);
        AddParam(cmd, "svc", "Nullable(String)", (object?)serviceName ?? DBNull.Value);
        AddParam(cmd, "l",   "UInt32",           (uint)limit);

        var list = new List<RouteStatusSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var service = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var route   = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var total   = ReadLong(reader, 2);
            var s2xx    = ReadLong(reader, 3);
            var s3xx    = ReadLong(reader, 4);
            var s4xx    = ReadLong(reader, 5);
            var s5xx    = ReadLong(reader, 6);
            var (p50, p99) = ReadQuantilesPair(reader, 7);
            var rate = total == 0 ? 0d : (double)(s4xx + s5xx) / total;
            list.Add(new RouteStatusSummary(service, route, total, s2xx, s3xx, s4xx, s5xx, rate, p50, p99));
        }
        return list;
    }

    public async Task<IReadOnlyList<StatusCodeBucket>> GetStatusBreakdownForRouteAsync(
        Guid tenantId, string serviceName, string httpRoute,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                http_status_code,
                sumMerge(request_count)                              AS total_req,
                quantilesTDigestMerge(0.5, 0.99)(latency_quantiles)  AS quants
              FROM kamsora_apm.status_hourly_rollup
             WHERE tenant_id = {t:UUID}
               AND service_name = {svc:String}
               AND http_route   = {rt:String}
               AND bucket_hour BETWEEN {f:DateTime} AND {tu:DateTime}
             GROUP BY http_status_code
             ORDER BY total_req DESC";

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",   "UUID",     tenantId);
        AddParam(cmd, "svc", "String",   serviceName);
        AddParam(cmd, "rt",  "String",   httpRoute);
        AddParam(cmd, "f",   "DateTime", fromUtc);
        AddParam(cmd, "tu",  "DateTime", toUtc);

        var list = new List<StatusCodeBucket>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var code  = (int)Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var total = ReadLong(reader, 1);
            var (p50, p99) = ReadQuantilesPair(reader, 2);
            list.Add(new StatusCodeBucket(code, total, p50, p99));
        }
        return list;
    }

    // ---- Helpers ---------------------------------------------------------

    private async Task<ClickHouseConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static long ReadLong(System.Data.Common.DbDataReader r, int i)
        => r.IsDBNull(i) ? 0L : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);

    /// <summary>
    /// Pulls a <c>quantilesTDigestMerge(0.5, 0.9, 0.99)</c> result into 3 ms values.
    /// ClickHouse.Client returns the array as <c>Single[]</c> (Float32) — match that
    /// and any future Float64 variant defensively.
    /// </summary>
    private static (double P50Ms, double P90Ms, double P99Ms) ReadQuantiles(System.Data.Common.DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return (0, 0, 0);
        var arr = ToDoubleArray(r.GetValue(i));
        double p50 = arr.Length > 0 ? arr[0] : 0;
        double p90 = arr.Length > 1 ? arr[1] : 0;
        double p99 = arr.Length > 2 ? arr[2] : 0;
        // Returned units are nanos (matched our duration_ns input) → convert to ms.
        return (Sanitize(p50 / 1_000_000d), Sanitize(p90 / 1_000_000d), Sanitize(p99 / 1_000_000d));
    }

    private static (double P50Ms, double P99Ms) ReadQuantilesPair(System.Data.Common.DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return (0, 0);
        var arr = ToDoubleArray(r.GetValue(i));
        double p50 = arr.Length > 0 ? arr[0] : 0;
        double p99 = arr.Length > 1 ? arr[1] : 0;
        return (Sanitize(p50 / 1_000_000d), Sanitize(p99 / 1_000_000d));
    }

    private static double[] ToDoubleArray(object value) => value switch
    {
        double[] d => d,
        float[]  f => Array.ConvertAll(f, x => (double)x),
        decimal[] m => Array.ConvertAll(m, x => (double)x),
        _ => Array.Empty<double>(),
    };

    private static double Sanitize(double v) => double.IsFinite(v) ? v : 0d;

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
