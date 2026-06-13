// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Storage.ClickHouse;

/// <summary>Read-side queries over <c>kamsora_apm.profiles</c>.</summary>
public sealed class ClickHouseProfileReader : IProfileReader
{
    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseProfileReader> _logger;

    public ClickHouseProfileReader(IOptions<ClickHouseOptions> options, ILogger<ClickHouseProfileReader> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<IReadOnlyList<ProfileCatalogEntry>> ListProfilesAsync(
        Guid tenantId, DateTime? fromUtc, DateTime? toUtc, string? serviceName, string? profileKind,
        int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 100;
        if (limit > 500) limit = 500;

        const string sql = @"
            SELECT start_timestamp, service_name, profile_kind,
                   duration_nanos, sample_count, length(pprof_bytes) AS pprof_size,
                   trigger_trace_id, agent_version
              FROM kamsora_apm.profiles
             WHERE tenant_id = {t:UUID}
               AND ({from_ms:Int64} = 0 OR toUnixTimestamp64Milli(start_timestamp) >= {from_ms:Int64})
               AND ({to_ms:Int64}   = 0 OR toUnixTimestamp64Milli(start_timestamp) <= {to_ms:Int64})
               AND ({svc:String}    = '' OR service_name = {svc:String})
               AND ({kind:String}   = '' OR profile_kind = {kind:String})
             ORDER BY start_timestamp DESC
             LIMIT {limit:UInt32}";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",       "UUID",   tenantId);
        AddParam(cmd, "from_ms", "Int64",  fromUtc.HasValue ? ToUnixMillis(fromUtc.Value) : 0L);
        AddParam(cmd, "to_ms",   "Int64",  toUtc.HasValue   ? ToUnixMillis(toUtc.Value)   : 0L);
        AddParam(cmd, "svc",     "String", serviceName ?? string.Empty);
        AddParam(cmd, "kind",    "String", profileKind ?? string.Empty);
        AddParam(cmd, "limit",   "UInt32", (uint)limit);

        var list = new List<ProfileCatalogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ProfileCatalogEntry(
                StartUtc:          DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                ServiceName:       reader.GetString(1),
                ProfileKind:       reader.GetString(2),
                DurationSeconds:   Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture) / 1_000_000_000.0,
                SampleCount:       Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                // length() on a Base64 string is ~4/3 of the original byte
                // count; surface the BASE64 length so the dashboard's
                // "size" column reflects what we actually transferred.
                PprofBytes:        Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                TriggerTraceIdHex: reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                AgentVersion:      reader.IsDBNull(7) ? string.Empty : reader.GetString(7)));
        }
        return list;
    }

    public async Task<ProfileBlob?> GetProfileBlobAsync(
        Guid tenantId, string serviceName, string profileKind, DateTime startUtc, CancellationToken ct)
    {
        // (tenant, service, kind, start_timestamp) is the prefix of the table's
        // ORDER BY, so this is an O(log n) point lookup — no full scan.
        const string sql = @"
            SELECT start_timestamp, service_name, profile_kind,
                   duration_nanos, sample_count, pprof_bytes
              FROM kamsora_apm.profiles
             WHERE tenant_id      = {t:UUID}
               AND service_name   = {svc:String}
               AND profile_kind   = {kind:String}
               AND start_timestamp = {start:DateTime64(9, 'UTC')}
             LIMIT 1";

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",     "UUID",   tenantId);
        AddParam(cmd, "svc",   "String", serviceName);
        AddParam(cmd, "kind",  "String", profileKind);
        AddParam(cmd, "start", "DateTime64", startUtc);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        var pprofB64 = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
        byte[] pprofBytes;
        try
        {
            pprofBytes = string.IsNullOrEmpty(pprofB64) ? Array.Empty<byte>() : Convert.FromBase64String(pprofB64);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "ClickHouse: profile row contains non-Base64 pprof_bytes; returning empty payload.");
            pprofBytes = Array.Empty<byte>();
        }

        return new ProfileBlob(
            StartUtc:        DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
            ServiceName:     reader.GetString(1),
            ProfileKind:     reader.GetString(2),
            DurationSeconds: Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture) / 1_000_000_000.0,
            SampleCount:     Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
            PprofBytes:      pprofBytes);
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
