// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Common.V1;
using KamsoraAPM.Contracts.Metrics.V1;
using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// Converts an inbound protobuf <see cref="Metric"/> + its enclosing context
/// into 1..N <see cref="MetricPointRow"/>s (one per data point).
/// </summary>
internal static class MetricRowMapper
{
    private const string AttrServiceName       = "service.name";
    private const string AttrServiceNamespace  = "service.namespace";
    private const string AttrAgentVersion      = "kamsora.agent.version";

    /// <summary>Returns one row per data point inside the metric.</summary>
    public static IEnumerable<MetricPointRow> ToRows(Guid tenantId, ResourceMetrics resourceMetrics, Metric metric)
    {
        var (serviceName, serviceNamespace, agentVersion) =
            ExtractResourceAttributes(resourceMetrics.Resource);

        switch (metric.DataCase)
        {
            case Metric.DataOneofCase.Gauge:
                foreach (var p in metric.Gauge.DataPoints)
                    yield return ScalarRow(tenantId, serviceName, serviceNamespace, agentVersion, metric, p, kind: "GAUGE",
                        temporality: "UNSPECIFIED", isMonotonic: false);
                break;

            case Metric.DataOneofCase.Sum:
                foreach (var p in metric.Sum.DataPoints)
                    yield return ScalarRow(tenantId, serviceName, serviceNamespace, agentVersion, metric, p, kind: "SUM",
                        temporality: MapTemporality(metric.Sum.AggregationTemporality),
                        isMonotonic: metric.Sum.IsMonotonic);
                break;

            case Metric.DataOneofCase.Histogram:
                foreach (var p in metric.Histogram.DataPoints)
                    yield return HistogramRow(tenantId, serviceName, serviceNamespace, agentVersion, metric, p,
                        temporality: MapTemporality(metric.Histogram.AggregationTemporality));
                break;
        }
    }

    private static MetricPointRow ScalarRow(
        Guid tenantId, string serviceName, string serviceNamespace, string agentVersion,
        Metric metric, NumberDataPoint p, string kind, string temporality, bool isMonotonic)
    {
        var row = new MetricPointRow
        {
            TenantId               = tenantId,
            // Clamp the point time to the Collector's clock window; the
            // cumulative-window start is left as-is (it legitimately points
            // at process start, hours in the past).
            TimeUnixNano           = TimestampGuard.ClampNanos(p.TimeUnixNano),
            StartTimeUnixNano      = p.StartTimeUnixNano == 0 ? p.TimeUnixNano : p.StartTimeUnixNano,
            ServiceName            = serviceName,
            ServiceNamespace       = serviceNamespace,
            MetricName             = metric.Name,
            MetricUnit             = metric.Unit ?? string.Empty,
            MetricKind             = kind,
            AggregationTemporality = temporality,
            IsMonotonic            = isMonotonic,
            AgentVersion           = agentVersion,
        };

        switch (p.ValueCase)
        {
            case NumberDataPoint.ValueOneofCase.AsDouble: row.ValueDouble = p.AsDouble; break;
            case NumberDataPoint.ValueOneofCase.AsInt:    row.ValueInt    = p.AsInt;    break;
        }

        AssignAttributes(row, p.Attributes);
        return row;
    }

    private static MetricPointRow HistogramRow(
        Guid tenantId, string serviceName, string serviceNamespace, string agentVersion,
        Metric metric, HistogramDataPoint p, string temporality)
    {
        var row = new MetricPointRow
        {
            TenantId               = tenantId,
            TimeUnixNano           = TimestampGuard.ClampNanos(p.TimeUnixNano),
            StartTimeUnixNano      = p.StartTimeUnixNano == 0 ? p.TimeUnixNano : p.StartTimeUnixNano,
            ServiceName            = serviceName,
            ServiceNamespace       = serviceNamespace,
            MetricName             = metric.Name,
            MetricUnit             = metric.Unit ?? string.Empty,
            MetricKind             = "HISTOGRAM",
            AggregationTemporality = temporality,
            IsMonotonic            = false,
            AgentVersion           = agentVersion,
            HistogramCount         = p.Count,
            HistogramSum           = p.HasSum ? p.Sum : null,
            HistogramMin           = p.HasMin ? p.Min : null,
            HistogramMax           = p.HasMax ? p.Max : null,
            HistogramBucketCounts  = p.BucketCounts.ToArray(),
            HistogramBucketBounds  = p.ExplicitBounds.ToArray(),
        };
        AssignAttributes(row, p.Attributes);
        return row;
    }

    private static void AssignAttributes(MetricPointRow row, Google.Protobuf.Collections.RepeatedField<KeyValue> attrs)
    {
        if (attrs.Count == 0) return;
        var keys   = new string[attrs.Count];
        var values = new string[attrs.Count];
        for (int i = 0; i < attrs.Count; i++)
        {
            keys[i]   = attrs[i].Key;
            values[i] = AnyValueToString(attrs[i].Value);
        }
        row.AttrsKeys   = keys;
        row.AttrsValues = values;
    }

    private static (string serviceName, string serviceNamespace, string agentVersion)
        ExtractResourceAttributes(Resource? resource)
    {
        string serviceName = string.Empty, serviceNamespace = string.Empty, agentVersion = string.Empty;
        if (resource is null) return (serviceName, serviceNamespace, agentVersion);

        foreach (var kv in resource.Attributes)
        {
            var v = AnyValueToString(kv.Value);
            switch (kv.Key)
            {
                case AttrServiceName:      serviceName      = v; break;
                case AttrServiceNamespace: serviceNamespace = v; break;
                case AttrAgentVersion:     agentVersion     = v; break;
            }
        }
        return (serviceName, serviceNamespace, agentVersion);
    }

    private static string MapTemporality(AggregationTemporality t) => t switch
    {
        AggregationTemporality.Delta      => "DELTA",
        AggregationTemporality.Cumulative => "CUMULATIVE",
        _                                 => "UNSPECIFIED",
    };

    private static string AnyValueToString(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.IntValue    => value.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.BoolValue   => value.BoolValue ? "true" : "false",
        _                                   => string.Empty,
    };
}
