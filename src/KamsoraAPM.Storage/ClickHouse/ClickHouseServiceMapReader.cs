// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Storage.ClickHouse;

/// <summary>
/// Service-map queries over <c>kamsora_apm.spans</c>. Four passes:
///   1. per-service node stats (volume, errors, p50)
///   2. service→service edges (parent/child spans across services in a trace)
///   3. service→database edges (CLIENT spans with db_system)
///   4. service→external edges (CLIENT HTTP spans grouped by URL host)
/// All bounded by the tenant + time window, so cost scales with the window's
/// span count, not table size.
/// </summary>
public sealed class ClickHouseServiceMapReader : IServiceMapReader
{
    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseServiceMapReader> _logger;

    public ClickHouseServiceMapReader(IOptions<ClickHouseOptions> options, ILogger<ClickHouseServiceMapReader> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<ServiceMapResult> GetServiceMapAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var nodes = new Dictionary<string, ServiceMapNode>(StringComparer.Ordinal);
        var edges = new List<ServiceMapEdge>();

        // ---- 1. Service nodes -------------------------------------------
        const string nodesSql = @"
            SELECT service_name,
                   count()                                                    AS calls,
                   countIf(status_code = 'ERROR' OR http_status_code >= 500)  AS errors,
                   quantile(0.5)(duration_ns) / 1e6                           AS p50_ms
              FROM kamsora_apm.spans
             WHERE tenant_id = {t:UUID}
               AND timestamp BETWEEN {f:DateTime} AND {tu:DateTime}
               AND service_name != ''
             GROUP BY service_name";

        await foreach (var row in QueryAsync(conn, nodesSql, tenantId, fromUtc, toUtc, ct))
        {
            var name = row.GetString(0);
            nodes[$"svc:{name}"] = new ServiceMapNode(
                Id:           $"svc:{name}",
                Label:        name,
                Kind:         "service",
                CallCount:    ToLong(row.GetValue(1)),
                ErrorCount:   ToLong(row.GetValue(2)),
                LatencyP50Ms: ToDouble(row.GetValue(3)));
        }

        // ---- 2. service -> service edges --------------------------------
        const string svcEdgesSql = @"
            SELECT p.service_name                                                   AS source,
                   c.service_name                                                   AS target,
                   count()                                                          AS calls,
                   countIf(c.status_code = 'ERROR' OR c.http_status_code >= 500)    AS errors,
                   avg(c.duration_ns) / 1e6                                         AS avg_ms
              FROM kamsora_apm.spans AS c
             INNER JOIN kamsora_apm.spans AS p
                ON p.tenant_id = c.tenant_id AND p.trace_id = c.trace_id AND p.span_id = c.parent_span_id
             WHERE c.tenant_id = {t:UUID}
               AND c.timestamp BETWEEN {f:DateTime} AND {tu:DateTime}
               AND p.timestamp BETWEEN {f:DateTime} AND {tu:DateTime}
               AND c.parent_span_id != ''
               AND p.service_name != '' AND c.service_name != ''
               AND p.service_name != c.service_name
             GROUP BY source, target";

        await foreach (var row in QueryAsync(conn, svcEdgesSql, tenantId, fromUtc, toUtc, ct))
        {
            edges.Add(new ServiceMapEdge(
                SourceId:     $"svc:{row.GetString(0)}",
                TargetId:     $"svc:{row.GetString(1)}",
                CallCount:    ToLong(row.GetValue(2)),
                ErrorCount:   ToLong(row.GetValue(3)),
                AvgLatencyMs: ToDouble(row.GetValue(4))));
        }

        // ---- 3. service -> database edges --------------------------------
        const string dbEdgesSql = @"
            SELECT service_name,
                   db_system,
                   count()                              AS calls,
                   countIf(status_code = 'ERROR')       AS errors,
                   avg(duration_ns) / 1e6               AS avg_ms
              FROM kamsora_apm.spans
             WHERE tenant_id = {t:UUID}
               AND timestamp BETWEEN {f:DateTime} AND {tu:DateTime}
               AND db_system != '' AND service_name != ''
             GROUP BY service_name, db_system";

        await foreach (var row in QueryAsync(conn, dbEdgesSql, tenantId, fromUtc, toUtc, ct))
        {
            var dbSystem = row.GetString(1);
            var dbId     = $"db:{dbSystem}";
            var calls    = ToLong(row.GetValue(2));
            var errors   = ToLong(row.GetValue(3));
            var avgMs    = ToDouble(row.GetValue(4));

            AccumulateNode(nodes, dbId, dbSystem, "database", calls, errors, avgMs);
            edges.Add(new ServiceMapEdge($"svc:{row.GetString(0)}", dbId, calls, errors, avgMs));
        }

        // ---- 4. service -> external HTTP edges ---------------------------
        const string extEdgesSql = @"
            SELECT service_name,
                   domain(http_url)                                              AS host,
                   count()                                                        AS calls,
                   countIf(status_code = 'ERROR' OR http_status_code >= 500)      AS errors,
                   avg(duration_ns) / 1e6                                         AS avg_ms
              FROM kamsora_apm.spans
             WHERE tenant_id = {t:UUID}
               AND timestamp BETWEEN {f:DateTime} AND {tu:DateTime}
               AND span_kind = 'CLIENT' AND db_system = '' AND http_url != ''
               AND domain(http_url) != '' AND service_name != ''
             GROUP BY service_name, host";

        await foreach (var row in QueryAsync(conn, extEdgesSql, tenantId, fromUtc, toUtc, ct))
        {
            var host   = row.GetString(1);
            var extId  = $"ext:{host}";
            var calls  = ToLong(row.GetValue(2));
            var errors = ToLong(row.GetValue(3));
            var avgMs  = ToDouble(row.GetValue(4));

            AccumulateNode(nodes, extId, host, "external", calls, errors, avgMs);
            edges.Add(new ServiceMapEdge($"svc:{row.GetString(0)}", extId, calls, errors, avgMs));
        }

        return new ServiceMapResult(nodes.Values.ToList(), edges);
    }

    /// <summary>Create or merge a database/external node from edge aggregates.</summary>
    private static void AccumulateNode(
        Dictionary<string, ServiceMapNode> nodes, string id, string label, string kind,
        long calls, long errors, double latencyMs)
    {
        if (nodes.TryGetValue(id, out var existing))
        {
            nodes[id] = existing with
            {
                CallCount  = existing.CallCount + calls,
                ErrorCount = existing.ErrorCount + errors,
            };
        }
        else
        {
            nodes[id] = new ServiceMapNode(id, label, kind, calls, errors, latencyMs);
        }
    }

    private async IAsyncEnumerable<System.Data.Common.DbDataReader> QueryAsync(
        ClickHouseConnection conn, string sql, Guid tenantId, DateTime fromUtc, DateTime toUtc,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",  "UUID",     tenantId);
        AddParam(cmd, "f",  "DateTime", fromUtc);
        AddParam(cmd, "tu", "DateTime", toUtc);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return reader;
        }
    }

    private static long ToLong(object v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    private static double ToDouble(object v) =>
        v is DBNull ? 0d : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, string chType, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value         = value;
        var typeProp = p.GetType().GetProperty("ClickHouseDbType");
        if (typeProp is not null && Enum.TryParse(typeProp.PropertyType, chType, true, out var typeEnum))
            typeProp.SetValue(p, typeEnum);
        cmd.Parameters.Add(p);
    }
}
