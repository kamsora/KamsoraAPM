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

/// <summary>Bulk-copies <see cref="ProfileRow"/> batches into <c>kamsora_apm.profiles</c>.</summary>
public sealed class ClickHouseProfileWriter : IProfileWriter
{
    private const string TableName = "kamsora_apm.profiles";

    // Order MUST match ToObjectArrays + the DestinationColumns list.
    private static readonly string[] Columns =
    {
        "tenant_id", "start_timestamp", "duration_nanos",
        "service_name", "service_namespace",
        "profile_kind", "sample_count",
        "pprof_bytes",
        "trigger_trace_id",
        "attrs_keys", "attrs_values",
        "agent_version",
    };

    private static readonly DateTime UnixEpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseProfileWriter> _logger;

    public ClickHouseProfileWriter(IOptions<ClickHouseOptions> options, ILogger<ClickHouseProfileWriter> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task WriteAsync(IReadOnlyList<ProfileRow> rows, CancellationToken cancellationToken)
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

        _logger.LogDebug("ClickHouse: inserted {Count} profile(s) into {Table}.", rows.Count, TableName);
    }

    private static IEnumerable<object?[]> ToObjectArrays(IReadOnlyList<ProfileRow> rows)
    {
        foreach (var r in rows)
        {
            var startTs    = UnixEpochUtc.AddTicks((long)(r.StartTimeUnixNano / 100UL));
            // ClickHouse.Client serializes `String` columns as UTF-8 from a
            // managed string. Base64 is the cheap, reversible encoding for
            // arbitrary bytes - see comment in 080_profiles.sql.
            var pprofB64   = r.PprofBytes is { Length: > 0 } ? Convert.ToBase64String(r.PprofBytes) : string.Empty;

            yield return new object?[]
            {
                r.TenantId, startTs, r.DurationNanos,
                r.ServiceName, r.ServiceNamespace,
                r.ProfileKind, r.SampleCount,
                pprofB64,
                r.TriggerTraceIdHex,
                r.AttrsKeys, r.AttrsValues,
                r.AgentVersion,
            };
        }
    }
}
