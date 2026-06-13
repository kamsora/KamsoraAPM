// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Storage.ClickHouse;

/// <summary>Read-side queries over <c>kamsora_apm.metric_points</c> + the minutely rollup.</summary>
public sealed class ClickHouseMetricReader : IMetricReader
{
    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseMetricReader> _logger;

    public ClickHouseMetricReader(IOptions<ClickHouseOptions> options, ILogger<ClickHouseMetricReader> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<IReadOnlyList<MetricCatalogEntry>> ListMetricsAsync(
        Guid tenantId, DateTime? fromUtc, DateTime? toUtc, string? serviceName, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                metric_name,
                anyLast(metric_kind) AS kind,
                anyLast(metric_unit) AS unit,
                service_name,
                max(timestamp)       AS last_seen,
                count()              AS pts
              FROM kamsora_apm.metric_points
             WHERE tenant_id = {t:UUID}
               AND ({from_ms:Int64} = 0 OR toUnixTimestamp64Milli(timestamp) >= {from_ms:Int64})
               AND ({to_ms:Int64}   = 0 OR toUnixTimestamp64Milli(timestamp) <= {to_ms:Int64})
               AND ({svc:String}    = '' OR service_name = {svc:String})
             GROUP BY metric_name, service_name
             ORDER BY last_seen DESC
             LIMIT 500";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",       "UUID",   tenantId);
        AddParam(cmd, "from_ms", "Int64",  fromUtc.HasValue ? ToUnixMillis(fromUtc.Value) : 0L);
        AddParam(cmd, "to_ms",   "Int64",  toUtc.HasValue   ? ToUnixMillis(toUtc.Value)   : 0L);
        AddParam(cmd, "svc",     "String", serviceName ?? string.Empty);

        var list = new List<MetricCatalogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new MetricCatalogEntry(
                MetricName:   reader.GetString(0),
                MetricKind:   reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                MetricUnit:   reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                ServiceName:  reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                LastSeenUtc:  DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                PointCount:   Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture)));
        }
        return list;
    }

    public async Task<IReadOnlyList<MetricSeriesPoint>> GetMetricSeriesAsync(
        Guid tenantId, string metricName, DateTime fromUtc, DateTime toUtc,
        string? serviceName, int bucketSeconds, CancellationToken ct)
    {
        if (bucketSeconds < 60) bucketSeconds = 60;

        // arrayStringConcat(arrayMap((k,v) -> concat(k,'=',v), attrs_keys, attrs_values), ',')
        // gives us a deterministic legend key per series.
        string sql = $@"
            SELECT
                toDateTime(intDiv(toUnixTimestamp(bucket_minute), {bucketSeconds}) * {bucketSeconds}, 'UTC') AS bucket,
                arrayStringConcat(arrayMap((k,v) -> concat(k,'=',v), attrs_keys, attrs_values), ',') AS series_key,
                anyLastMerge(value_last) AS v_last,
                maxMerge(value_max)      AS v_max,
                minMerge(value_min)      AS v_min
              FROM kamsora_apm.metrics_minutely_rollup
             WHERE tenant_id   = {{t:UUID}}
               AND metric_name = {{name:String}}
               AND bucket_minute BETWEEN {{f:DateTime}} AND {{tu:DateTime}}
               AND ({{svc:String}} = '' OR service_name = {{svc:String}})
             GROUP BY bucket, series_key, attrs_keys, attrs_values
             ORDER BY bucket";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",    "UUID",     tenantId);
        AddParam(cmd, "name", "String",   metricName);
        AddParam(cmd, "f",    "DateTime", fromUtc);
        AddParam(cmd, "tu",   "DateTime", toUtc);
        AddParam(cmd, "svc",  "String",   serviceName ?? string.Empty);

        var list = new List<MetricSeriesPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new MetricSeriesPoint(
                BucketStartUtc: DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                SeriesKey:      reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                ValueLast:      ReadDouble(reader, 2),
                ValueMax:       ReadDouble(reader, 3),
                ValueMin:       ReadDouble(reader, 4)));
        }
        return list;
    }

    private static double ReadDouble(System.Data.Common.DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return 0d;
        var v = Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        return double.IsFinite(v) ? v : 0d;
    }

    private static long ToUnixMillis(DateTime utc)
    {
        var d = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return new DateTimeOffset(d).ToUnixTimeMilliseconds();
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, string chType, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value         = value;
        var typeProp = p.GetType().GetProperty("ClickHouseDbType");
        if (typeProp is not null)
        {
            var inner = chType.Split('(')[0];
            if (Enum.TryParse(typeProp.PropertyType, inner, true, out var typeEnum))
                typeProp.SetValue(p, typeEnum);
        }
        cmd.Parameters.Add(p);
    }
}
