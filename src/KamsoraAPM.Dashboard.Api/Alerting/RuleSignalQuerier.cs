// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Dashboard.Api.Alerting;

/// <summary>
/// Resolves the current value of a rule's signal by querying ClickHouse over
/// the last <c>WindowSeconds</c>. Single class for all 5 supported signals -
/// keeps the engine straightforward; no per-signal strategy plumbing.
/// </summary>
public sealed class RuleSignalQuerier
{
    private readonly ClickHouseOptions _options;
    private readonly ILogger<RuleSignalQuerier> _logger;

    public RuleSignalQuerier(IOptions<ClickHouseOptions> options, ILogger<RuleSignalQuerier> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<double> QueryAsync(RuleDefinition rule, DateTime nowUtc, CancellationToken ct)
    {
        var fromUtc = nowUtc.AddSeconds(-Math.Max(60, rule.WindowSeconds));

        var sql = rule.SignalType switch
        {
            SignalTypes.LatencyP50    => LatencyQuantileSql("0.5"),
            SignalTypes.LatencyP90    => LatencyQuantileSql("0.9"),
            SignalTypes.LatencyP99    => LatencyQuantileSql("0.99"),
            SignalTypes.ErrorRate     => ErrorRateSql,
            SignalTypes.RequestVolume => RequestVolumeSql,
            SignalTypes.LogCount      => LogCountSql,
            SignalTypes.MetricAvg     => MetricSql("avg"),
            SignalTypes.MetricMax     => MetricSql("max"),
            _ => throw new InvalidOperationException($"Unsupported signal_type '{rule.SignalType}'."),
        };

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddParam(cmd, "t",   "UUID",                  rule.TenantId);
        AddParam(cmd, "f",   "DateTime64(9, 'UTC')",  fromUtc);
        AddParam(cmd, "tu",  "DateTime64(9, 'UTC')",  nowUtc);
        AddParam(cmd, "svc", "String",                rule.ServiceFilter ?? string.Empty);
        if (rule.SignalType == SignalTypes.LogCount)
        {
            AddParam(cmd, "sevFloor", "UInt8", (byte)LogSeverityFloors.Resolve(rule.SignalParam));
        }
        else if (SignalTypes.RequiresMetricName(rule.SignalType))
        {
            AddParam(cmd, "metric", "String", rule.SignalParam ?? string.Empty);
        }

        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is null || raw is DBNull) return 0d;

        var value = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(value)) value = 0d;
        return value;
    }

    private const string ErrorRateSql = @"
        SELECT
            if(count() = 0, 0,
               countIf(status_code = 'ERROR' OR http_status_code >= 500) / toFloat64(count()))
          FROM kamsora_apm.spans
         WHERE tenant_id = {t:UUID}
           AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
           AND span_kind = 'SERVER'
           AND ({svc:String} = '' OR service_name = {svc:String})";

    private const string RequestVolumeSql = @"
        SELECT count()
          FROM kamsora_apm.spans
         WHERE tenant_id = {t:UUID}
           AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
           AND span_kind = 'SERVER'
           AND ({svc:String} = '' OR service_name = {svc:String})";

    private const string LogCountSql = @"
        SELECT count()
          FROM kamsora_apm.logs
         WHERE tenant_id = {t:UUID}
           AND timestamp BETWEEN {f:DateTime64(9, 'UTC')} AND {tu:DateTime64(9, 'UTC')}
           AND severity_number >= {sevFloor:UInt8}
           AND ({svc:String} = '' OR service_name = {svc:String})";

    /// <summary>
    /// Aggregates a metric's scalar reading (gauge/sum value, falling back to
    /// histogram_sum for histogram metrics) over the window.
    /// </summary>
    private static string MetricSql(string agg) => $@"
        SELECT {agg}(coalesce(value_double, toFloat64(value_int), histogram_sum))
          FROM kamsora_apm.metric_points
         WHERE tenant_id = {{t:UUID}}
           AND timestamp BETWEEN {{f:DateTime64(9, 'UTC')}} AND {{tu:DateTime64(9, 'UTC')}}
           AND metric_name = {{metric:String}}
           AND ({{svc:String}} = '' OR service_name = {{svc:String}})";

    /// <summary>Returns the percentile in ms (matches the dashboard's display unit).</summary>
    private static string LatencyQuantileSql(string q) => $@"
        SELECT quantile({q})(duration_ns) / 1000000.0
          FROM kamsora_apm.spans
         WHERE tenant_id = {{t:UUID}}
           AND timestamp BETWEEN {{f:DateTime64(9, 'UTC')}} AND {{tu:DateTime64(9, 'UTC')}}
           AND span_kind = 'SERVER'
           AND ({{svc:String}} = '' OR service_name = {{svc:String}})";

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

    public static bool CompareThreshold(string op, double observed, double threshold) => op switch
    {
        "gt"  => observed >  threshold,
        "gte" => observed >= threshold,
        "lt"  => observed <  threshold,
        "lte" => observed <= threshold,
        "eq"  => Math.Abs(observed - threshold) < double.Epsilon,
        _     => false,
    };
}
