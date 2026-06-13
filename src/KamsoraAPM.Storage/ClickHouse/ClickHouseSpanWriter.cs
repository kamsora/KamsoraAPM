// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Data;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Models;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Storage.ClickHouse;

/// <summary>
/// Bulk inserts <see cref="SpanRow"/> batches into <c>kamsora_apm.spans</c>
/// using ClickHouse's native bulk-copy path. No ORMs anywhere on the ingestion
/// path — see ADR-0004.
/// </summary>
public sealed class ClickHouseSpanWriter : ISpanWriter
{
    private const string TableName = "kamsora_apm.spans";

    // Column order MUST match the SELECT below and the bulk-copy DestinationColumns
    // list. Update all three together if the schema changes.
    private static readonly string[] Columns =
    {
        "tenant_id",
        "timestamp",
        "trace_id",
        "span_id",
        "parent_span_id",
        "trace_state",
        "service_name",
        "service_namespace",
        "service_version",
        "span_name",
        "span_kind",
        "start_time_unix_ns",
        "end_time_unix_ns",
        "status_code",
        "status_message",
        "http_method",
        "http_status_code",
        "http_route",
        "http_url",
        "http_client_ip",
        "consumer_id",
        "db_system",
        "db_statement",
        "db_duration_ns",
        "attrs_keys",
        "attrs_values",
        "event_names",
        "event_times_unix_ns",
        "event_attrs_json",
        "agent_version",
    };

    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseSpanWriter> _logger;

    public ClickHouseSpanWriter(IOptions<ClickHouseOptions> options, ILogger<ClickHouseSpanWriter> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task WriteAsync(IReadOnlyList<SpanRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return;

        await using var connection = new ClickHouseConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var bulkCopy = new ClickHouseBulkCopy(connection)
        {
            DestinationTableName = TableName,
            ColumnNames          = Columns,
            BatchSize            = rows.Count,
            MaxDegreeOfParallelism = 1,
        };

        await bulkCopy.InitAsync().ConfigureAwait(false);
        await bulkCopy.WriteToServerAsync(ToObjectArrays(rows), cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("ClickHouse: inserted {Count} span(s) into {Table}.", rows.Count, TableName);
    }

    private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<object?[]> ToObjectArrays(IReadOnlyList<SpanRow> rows)
    {
        foreach (var r in rows)
        {
            // Timestamp column is DateTime64(9, 'UTC') — ClickHouse.Client's BulkCopy
            // path requires a DateTime / DateTimeOffset, not a decimal. Convert from
            // unix nanos (100 ns ticks since the unix epoch).
            var timestamp = UnixEpochUtc.AddTicks((long)(r.StartTimeUnixNano / 100UL));

            yield return new object?[]
            {
                r.TenantId,
                timestamp,
                Convert.ToHexString(r.TraceId).ToLowerInvariant(),
                Convert.ToHexString(r.SpanId).ToLowerInvariant(),
                r.ParentSpanId.Length == 0 ? string.Empty : Convert.ToHexString(r.ParentSpanId).ToLowerInvariant(),
                r.TraceState,
                r.ServiceName,
                r.ServiceNamespace,
                r.ServiceVersion,
                r.SpanName,
                r.SpanKind,
                r.StartTimeUnixNano,
                r.EndTimeUnixNano,
                r.StatusCode,
                r.StatusMessage,
                r.HttpMethod,
                (int)r.HttpStatusCode,
                r.HttpRoute,
                r.HttpUrl,
                r.HttpClientIp,
                r.ConsumerId,
                r.DbSystem,
                r.DbStatement,
                r.DbDurationNs,
                r.AttrsKeys,
                r.AttrsValues,
                r.EventNames,
                r.EventTimesUnixNs,
                r.EventAttrsJson,
                r.AgentVersion,
            };
        }
    }
}
