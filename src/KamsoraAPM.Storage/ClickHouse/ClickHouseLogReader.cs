// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Storage.ClickHouse;

/// <summary>Read-side queries over <c>kamsora_apm.logs</c> + the M8 rollups.</summary>
public sealed class ClickHouseLogReader : ILogReader
{
    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseLogReader> _logger;

    public ClickHouseLogReader(IOptions<ClickHouseOptions> options, ILogger<ClickHouseLogReader> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<IReadOnlyList<LogRowDto>> ListLogsAsync(
        Guid tenantId, DateTime? fromUtc, DateTime? toUtc,
        string? serviceName, byte? minSeverity, string? bodySearch, string? traceIdHex,
        int limit, ulong cursorTimeUnixNano, CancellationToken ct)
    {
        if (limit <= 0) limit = 100;
        if (limit > 1000) limit = 1000;

        const string sql = @"
            SELECT timestamp, service_name, severity_number, severity_text, body,
                   trace_id AS trace_id_hex, span_id AS span_id_hex,
                   attrs_keys, attrs_values
              FROM kamsora_apm.logs
             WHERE tenant_id = {t:UUID}
               AND ({from_ms:Int64} = 0 OR toUnixTimestamp64Milli(timestamp) >= {from_ms:Int64})
               AND ({to_ms:Int64}   = 0 OR toUnixTimestamp64Milli(timestamp) <= {to_ms:Int64})
               AND ({svc:String}    = '' OR service_name = {svc:String})
               AND ({min_sev:UInt8} = 0  OR severity_number >= {min_sev:UInt8})
               AND ({body:String}   = '' OR positionCaseInsensitive(body, {body:String}) > 0)
               AND ({trace_set:UInt8} = 0 OR trace_id = {trace:String})
               AND ({cursor_ms:Int64} = 0 OR toUnixTimestamp64Milli(timestamp) < {cursor_ms:Int64})
             ORDER BY timestamp DESC
             LIMIT {limit:UInt32}";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",         "UUID",   tenantId);
        AddParam(cmd, "from_ms",   "Int64",  fromUtc.HasValue ? ToUnixMillis(fromUtc.Value) : 0L);
        AddParam(cmd, "to_ms",     "Int64",  toUtc.HasValue   ? ToUnixMillis(toUtc.Value)   : 0L);
        AddParam(cmd, "svc",       "String", serviceName ?? string.Empty);
        AddParam(cmd, "min_sev",   "UInt8",  (byte)(minSeverity ?? 0));
        AddParam(cmd, "body",      "String", bodySearch ?? string.Empty);
        AddParam(cmd, "trace_set", "UInt8",  (byte)(string.IsNullOrEmpty(traceIdHex) ? 0 : 1));
        AddParam(cmd, "trace",     "String", (traceIdHex ?? string.Empty).ToLowerInvariant());
        AddParam(cmd, "cursor_ms", "Int64",  cursorTimeUnixNano == 0 ? 0L : (long)(cursorTimeUnixNano / 1_000_000UL));
        AddParam(cmd, "limit",     "UInt32", (uint)limit);

        var list = new List<LogRowDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadLog(reader));
        }
        return list;
    }

    public async Task<IReadOnlyList<LogRowDto>> GetLogsForTraceAsync(
        Guid tenantId, string traceIdHex, CancellationToken ct)
    {
        const string sql = @"
            SELECT timestamp, service_name, severity_number, severity_text, body,
                   trace_id AS trace_id_hex, span_id AS span_id_hex,
                   attrs_keys, attrs_values
              FROM kamsora_apm.logs
             WHERE tenant_id = {t:UUID}
               AND trace_id = {trace:String}
             ORDER BY timestamp ASC
             LIMIT 500";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",     "UUID",   tenantId);
        AddParam(cmd, "trace", "String", (traceIdHex ?? string.Empty).ToLowerInvariant());

        var list = new List<LogRowDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadLog(reader));
        }
        return list;
    }

    public async Task<IReadOnlyList<LogVolumePoint>> GetLogVolumeTimeseriesAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc,
        string? serviceName, int bucketSeconds, CancellationToken ct)
    {
        if (bucketSeconds < 60) bucketSeconds = 60;   // rollup grain
        string sql = $@"
            SELECT
                toDateTime(intDiv(toUnixTimestamp(bucket_minute), {bucketSeconds}) * {bucketSeconds}, 'UTC') AS bucket,
                severity_text,
                sumMerge(log_count) AS cnt
              FROM kamsora_apm.logs_minutely_rollup
             WHERE tenant_id = {{t:UUID}}
               AND bucket_minute BETWEEN {{f:DateTime}} AND {{tu:DateTime}}
               AND ({{svc:String}} = '' OR service_name = {{svc:String}})
             GROUP BY bucket, severity_text
             ORDER BY bucket";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",   "UUID",     tenantId);
        AddParam(cmd, "f",   "DateTime", fromUtc);
        AddParam(cmd, "tu",  "DateTime", toUtc);
        AddParam(cmd, "svc", "String",   serviceName ?? string.Empty);

        var list = new List<LogVolumePoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new LogVolumePoint(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)));
        }
        return list;
    }

    // ---- Helpers ---------------------------------------------------------

    private static LogRowDto ReadLog(System.Data.Common.DbDataReader r)
    {
        var keys   = r.IsDBNull(7) ? Array.Empty<string>() : (string[])r.GetValue(7);
        var values = r.IsDBNull(8) ? Array.Empty<string>() : (string[])r.GetValue(8);
        var attrs  = new Dictionary<string, string>(keys.Length, StringComparer.Ordinal);
        if (keys.Length == values.Length)
        {
            for (int i = 0; i < keys.Length; i++) attrs[keys[i]] = values[i];
        }

        return new LogRowDto(
            TimestampUtc:    DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc),
            ServiceName:     r.IsDBNull(1) ? string.Empty : r.GetString(1),
            SeverityNumber:  Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture),
            SeverityText:    r.IsDBNull(3) ? string.Empty : r.GetString(3),
            Body:            r.IsDBNull(4) ? string.Empty : r.GetString(4),
            TraceIdHex:      NormaliseHex(r.IsDBNull(5) ? string.Empty : r.GetString(5)),
            SpanIdHex:       NormaliseHex(r.IsDBNull(6) ? string.Empty : r.GetString(6)),
            Attributes:      attrs);
    }

    private static string NormaliseHex(string s)
    {
        // Writer stores empty string for unset IDs; just lower-case defensively.
        return string.IsNullOrEmpty(s) ? string.Empty : s.ToLowerInvariant();
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
