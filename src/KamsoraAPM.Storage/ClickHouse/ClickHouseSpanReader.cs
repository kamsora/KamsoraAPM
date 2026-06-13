// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Data;
using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Models;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Storage.ClickHouse;

/// <summary>
/// Reads <see cref="SpanRow"/>s from <c>kamsora_apm.spans</c> for the Dashboard.Api.
/// Every query is tenant-scoped — the tenant_id always sits at the head of the
/// <c>WHERE</c> clause so ClickHouse can prune partitions.
/// </summary>
public sealed class ClickHouseSpanReader : ISpanReader
{
    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseSpanReader> _logger;

    public ClickHouseSpanReader(IOptions<ClickHouseOptions> options, ILogger<ClickHouseSpanReader> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public Task<IReadOnlyList<SpanRow>> ListRecentAsync(
        Guid tenantId, string? serviceName, string? spanKind,
        DateTime? fromUtc, DateTime? toUtc,
        int limit, ulong cursorTimeUnixNano, CancellationToken cancellationToken)
        => ListRecentAsync(tenantId, serviceName, spanKind, fromUtc, toUtc,
            consumerId: null, httpRoute: null, errorsOnly: false,
            limit, cursorTimeUnixNano, cancellationToken);

    public async Task<IReadOnlyList<SpanRow>> ListRecentAsync(
        Guid tenantId,
        string? serviceName,
        string? spanKind,
        DateTime? fromUtc,
        DateTime? toUtc,
        string? consumerId,
        string? httpRoute,
        bool errorsOnly,
        int limit,
        ulong cursorTimeUnixNano,
        CancellationToken cancellationToken)
    {
        if (limit <= 0) limit = 50;
        if (limit > 1000) limit = 1000;

        // "(anonymous)" is the synthetic display label the Dashboard.Api inserts
        // for spans whose Agent extractor returned empty. Map it back here so the
        // drill-through filter actually matches those rows.
        var consumerFilter = string.Equals(consumerId, "(anonymous)", StringComparison.Ordinal)
            ? string.Empty
            : consumerId;
        var matchEmptyConsumer = consumerId is not null && string.IsNullOrEmpty(consumerFilter);

        await using var connection = new ClickHouseConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Sentinel-0 pattern for optional time filters — declaring a parameter
        // as both Nullable(T) and T in the same query trips ClickHouse's
        // BAD_QUERY_PARAMETER. Sending a literal 0 from C# keeps the type stable.
        // For consumer_id we use {consumer_set:UInt8} as a "filter active" flag
        // to distinguish "no filter" from "filter on empty string" (anonymous).
        const string sql = @"
            SELECT tenant_id, timestamp, trace_id, span_id, parent_span_id, trace_state,
                   service_name, service_namespace, service_version, span_name, span_kind,
                   start_time_unix_ns, end_time_unix_ns, status_code, status_message,
                   http_method, http_status_code, http_route, http_url, http_client_ip,
                   consumer_id,
                   db_system, db_statement, db_duration_ns, attrs_keys, attrs_values,
                   event_names, event_times_unix_ns, event_attrs_json, agent_version
              FROM kamsora_apm.spans
             WHERE tenant_id = {tenant_id:UUID}
               AND ({service:String} = '' OR service_name = {service:String})
               AND ({kind:String}    = '' OR span_kind    = {kind:String})
               AND ({route:String}   = '' OR http_route   = {route:String})
               AND ({consumer_set:UInt8} = 0 OR consumer_id = {consumer:String})
               AND ({errors_only:UInt8} = 0 OR status_code = 'ERROR' OR http_status_code >= 400)
               AND ({from_ns:UInt64} = 0  OR start_time_unix_ns >= {from_ns:UInt64})
               AND ({to_ns:UInt64}   = 0  OR start_time_unix_ns <= {to_ns:UInt64})
               AND ({cursor_ns:UInt64} = 0 OR start_time_unix_ns < {cursor_ns:UInt64})
             ORDER BY start_time_unix_ns DESC
             LIMIT {limit:UInt32}";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        cmd.AddParameter("tenant_id",   "UUID",   tenantId);
        cmd.AddParameter("service",     "String", serviceName ?? string.Empty);
        cmd.AddParameter("kind",        "String", spanKind    ?? string.Empty);
        cmd.AddParameter("route",       "String", httpRoute   ?? string.Empty);
        cmd.AddParameter("consumer_set","UInt8",  (byte)(consumerId is null ? 0 : 1));
        cmd.AddParameter("consumer",    "String", consumerFilter ?? string.Empty);
        cmd.AddParameter("errors_only", "UInt8",  (byte)(errorsOnly ? 1 : 0));
        cmd.AddParameter("from_ns",     "UInt64", fromUtc.HasValue ? ToUnixNanos(fromUtc.Value) : 0UL);
        cmd.AddParameter("to_ns",       "UInt64", toUtc.HasValue   ? ToUnixNanos(toUtc.Value)   : 0UL);
        cmd.AddParameter("cursor_ns",   "UInt64", cursorTimeUnixNano);
        cmd.AddParameter("limit",       "UInt32", (uint)limit);
        _ = matchEmptyConsumer; // kept for symmetry; consumer_set+consumer covers it

        var results = new List<SpanRow>(limit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRow(reader));
        }

        _logger.LogDebug("ClickHouse: read {Count} span(s) for tenant {Tenant}.", results.Count, tenantId);
        return results;
    }

    private static SpanRow ReadRow(System.Data.Common.DbDataReader r)
    {
        return new SpanRow
        {
            TenantId          = r.GetGuid(0),
            // UInt64 / UInt16 columns must NOT use GetInt64/GetInt32 — those throw
            // InvalidCast on unsigned types. Convert.* handles both signed/unsigned.
            StartTimeUnixNano = ReadULong(r, 11),
            EndTimeUnixNano   = ReadULong(r, 12),
            TraceId           = r.IsDBNull(2) ? Array.Empty<byte>() : HexToBytes(r.GetString(2)),
            SpanId            = r.IsDBNull(3) ? Array.Empty<byte>() : HexToBytes(r.GetString(3)),
            ParentSpanId      = r.IsDBNull(4) ? Array.Empty<byte>() : HexToBytes(r.GetString(4)),
            TraceState        = r.IsDBNull(5) ? string.Empty : r.GetString(5),
            ServiceName       = r.IsDBNull(6) ? string.Empty : r.GetString(6),
            ServiceNamespace  = r.IsDBNull(7) ? string.Empty : r.GetString(7),
            ServiceVersion    = r.IsDBNull(8) ? string.Empty : r.GetString(8),
            SpanName          = r.IsDBNull(9) ? string.Empty : r.GetString(9),
            SpanKind          = r.IsDBNull(10) ? "INTERNAL" : r.GetString(10),
            StatusCode        = r.IsDBNull(13) ? "UNSET" : r.GetString(13),
            StatusMessage     = r.IsDBNull(14) ? string.Empty : r.GetString(14),
            HttpMethod        = r.IsDBNull(15) ? string.Empty : r.GetString(15),
            HttpStatusCode    = ReadUShort(r, 16),
            HttpRoute         = r.IsDBNull(17) ? string.Empty : r.GetString(17),
            HttpUrl           = r.IsDBNull(18) ? string.Empty : r.GetString(18),
            HttpClientIp      = r.IsDBNull(19) ? string.Empty : r.GetString(19),
            ConsumerId        = r.IsDBNull(20) ? string.Empty : r.GetString(20),
            DbSystem          = r.IsDBNull(21) ? string.Empty : r.GetString(21),
            DbStatement       = r.IsDBNull(22) ? string.Empty : r.GetString(22),
            DbDurationNs      = ReadULong(r, 23),
            AttrsKeys         = r.IsDBNull(24) ? Array.Empty<string>() : (string[])r.GetValue(24),
            AttrsValues       = r.IsDBNull(25) ? Array.Empty<string>() : (string[])r.GetValue(25),
            EventNames        = r.IsDBNull(26) ? Array.Empty<string>() : (string[])r.GetValue(26),
            EventTimesUnixNs  = r.IsDBNull(27) ? Array.Empty<ulong>()  : ((ulong[])r.GetValue(27)),
            EventAttrsJson    = r.IsDBNull(28) ? Array.Empty<string>() : (string[])r.GetValue(28),
            AgentVersion      = r.IsDBNull(29) ? string.Empty : r.GetString(29),
        };
    }

    private static ulong ReadULong(System.Data.Common.DbDataReader r, int i) =>
        r.IsDBNull(i) ? 0UL : Convert.ToUInt64(r.GetValue(i));

    private static ushort ReadUShort(System.Data.Common.DbDataReader r, int i) =>
        r.IsDBNull(i) ? (ushort)0 : Convert.ToUInt16(r.GetValue(i));

    private static byte[] HexToBytes(string hex) =>
        string.IsNullOrEmpty(hex) ? Array.Empty<byte>() : Convert.FromHexString(hex);

    private static ulong ToUnixNanos(DateTime utc)
    {
        const long UnixEpochTicks = 621_355_968_000_000_000L;
        long ticks = utc.Kind == DateTimeKind.Utc ? utc.Ticks : utc.ToUniversalTime().Ticks;
        long delta = ticks - UnixEpochTicks;
        return delta <= 0 ? 0UL : (ulong)delta * 100UL;
    }
}

internal static class ClickHouseDbCommandExtensions
{
    /// <summary>Helper that abstracts ClickHouse.Client's parameter-binding API.</summary>
    public static void AddParameter(this System.Data.Common.DbCommand cmd, string name, string clickHouseType, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value         = value;
        // ClickHouse.Client exposes a ClickHouseDbType setter on its native parameter;
        // we use reflection here to avoid taking a hard reference to the concrete
        // ClickHouseDbParameter type in this helper. This is a hot-ish path on reads;
        // M2 replaces this with a typed adapter once we lock the read query shape.
        var typeProp = p.GetType().GetProperty("ClickHouseDbType");
        if (typeProp is not null && Enum.TryParse(typeProp.PropertyType, NormalizeType(clickHouseType), out var typeEnum))
        {
            typeProp.SetValue(p, typeEnum);
        }
        cmd.Parameters.Add(p);
    }

    private static string NormalizeType(string t)
    {
        // The ClickHouseDbType enum members are PascalCase, e.g. "UUID" -> "UUID",
        // "Nullable(String)" -> string after stripping Nullable wrapper which the
        // type system handles separately via value = DBNull.Value.
        var inner = t.StartsWith("Nullable(", StringComparison.Ordinal) && t.EndsWith(')')
            ? t.Substring("Nullable(".Length, t.Length - "Nullable(".Length - 1)
            : t;
        return inner;
    }
}
