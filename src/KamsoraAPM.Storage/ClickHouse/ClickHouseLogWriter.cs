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

/// <summary>Bulk-copies <see cref="LogRow"/> batches into <c>kamsora_apm.logs</c>.</summary>
public sealed class ClickHouseLogWriter : ILogWriter
{
    private const string TableName = "kamsora_apm.logs";

    // Order MUST match ToObjectArrays + the DestinationColumns list.
    private static readonly string[] Columns =
    {
        "tenant_id", "timestamp", "observed_timestamp",
        "service_name", "service_namespace",
        "severity_number", "severity_text", "body",
        "trace_id", "span_id",
        "attrs_keys", "attrs_values",
        "agent_version",
    };

    private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseLogWriter> _logger;

    public ClickHouseLogWriter(IOptions<ClickHouseOptions> options, ILogger<ClickHouseLogWriter> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task WriteAsync(IReadOnlyList<LogRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return;

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var bulk = new ClickHouseBulkCopy(conn)
        {
            DestinationTableName   = TableName,
            ColumnNames            = Columns,
            BatchSize              = rows.Count,
            MaxDegreeOfParallelism = 1,
        };
        await bulk.InitAsync().ConfigureAwait(false);
        await bulk.WriteToServerAsync(ToObjectArrays(rows), cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("ClickHouse: inserted {Count} log(s) into {Table}.", rows.Count, TableName);
    }

    private static IEnumerable<object?[]> ToObjectArrays(IReadOnlyList<LogRow> rows)
    {
        foreach (var r in rows)
        {
            var ts        = UnixEpochUtc.AddTicks((long)(r.TimeUnixNano         / 100UL));
            var observed  = UnixEpochUtc.AddTicks((long)(r.ObservedTimeUnixNano / 100UL));
            // Match the spans-table convention: hex-encode binary IDs as
            // lowercase strings. ClickHouse's bulk-copy serializer can't push
            // byte[] into FixedString - see schema comment in 030_logs.sql.
            var traceId   = HexOrEmpty(r.TraceId);
            var spanId    = HexOrEmpty(r.SpanId);

            yield return new object?[]
            {
                r.TenantId, ts, observed,
                r.ServiceName, r.ServiceNamespace,
                (int)r.SeverityNumber, r.SeverityText, r.Body,
                traceId, spanId,
                r.AttrsKeys, r.AttrsValues,
                r.AgentVersion,
            };
        }
    }

    private static string HexOrEmpty(byte[] src)
    {
        if (src is null || src.Length == 0) return string.Empty;
        return Convert.ToHexString(src).ToLowerInvariant();
    }
}

/// <summary>Bulk-copies <see cref="MetricPointRow"/> batches into <c>kamsora_apm.metric_points</c>.</summary>
public sealed class ClickHouseMetricWriter : IMetricWriter
{
    private const string TableName = "kamsora_apm.metric_points";

    private static readonly string[] Columns =
    {
        "tenant_id", "timestamp", "start_timestamp",
        "service_name", "service_namespace",
        "metric_name", "metric_unit", "metric_kind", "aggregation_temporality", "is_monotonic",
        "value_double", "value_int",
        "histogram_count", "histogram_sum", "histogram_min", "histogram_max",
        "histogram_bucket_counts", "histogram_bucket_bounds",
        "attrs_keys", "attrs_values",
        "agent_version",
    };

    private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseMetricWriter> _logger;

    public ClickHouseMetricWriter(IOptions<ClickHouseOptions> options, ILogger<ClickHouseMetricWriter> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task WriteAsync(IReadOnlyList<MetricPointRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return;

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var bulk = new ClickHouseBulkCopy(conn)
        {
            DestinationTableName   = TableName,
            ColumnNames            = Columns,
            BatchSize              = rows.Count,
            MaxDegreeOfParallelism = 1,
        };
        await bulk.InitAsync().ConfigureAwait(false);
        await bulk.WriteToServerAsync(ToObjectArrays(rows), cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("ClickHouse: inserted {Count} metric point(s) into {Table}.", rows.Count, TableName);
    }

    private static IEnumerable<object?[]> ToObjectArrays(IReadOnlyList<MetricPointRow> rows)
    {
        foreach (var r in rows)
        {
            var ts    = UnixEpochUtc.AddTicks((long)(r.TimeUnixNano      / 100UL));
            var start = UnixEpochUtc.AddTicks((long)(r.StartTimeUnixNano / 100UL));

            yield return new object?[]
            {
                r.TenantId, ts, start,
                r.ServiceName, r.ServiceNamespace,
                r.MetricName, r.MetricUnit, r.MetricKind, r.AggregationTemporality,
                (byte)(r.IsMonotonic ? 1 : 0),
                r.ValueDouble.HasValue ? (object)r.ValueDouble.Value : DBNull.Value,
                r.ValueInt.HasValue    ? (object)r.ValueInt.Value    : DBNull.Value,
                r.HistogramCount.HasValue ? (object)r.HistogramCount.Value : DBNull.Value,
                r.HistogramSum.HasValue   ? (object)r.HistogramSum.Value   : DBNull.Value,
                r.HistogramMin.HasValue   ? (object)r.HistogramMin.Value   : DBNull.Value,
                r.HistogramMax.HasValue   ? (object)r.HistogramMax.Value   : DBNull.Value,
                r.HistogramBucketCounts,
                r.HistogramBucketBounds,
                r.AttrsKeys, r.AttrsValues,
                r.AgentVersion,
            };
        }
    }
}
