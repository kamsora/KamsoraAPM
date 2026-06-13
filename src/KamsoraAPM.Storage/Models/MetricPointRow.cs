// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Models;

/// <summary>
/// Storage-shaped representation of one OTLP metric data point. One row per
/// (metric, attribute set, point) — for histograms we keep the buckets inline.
/// </summary>
public sealed class MetricPointRow
{
    public Guid    TenantId         { get; set; }
    public ulong   TimeUnixNano     { get; set; }
    public ulong   StartTimeUnixNano { get; set; }

    public string  ServiceName      { get; set; } = string.Empty;
    public string  ServiceNamespace { get; set; } = string.Empty;

    public string  MetricName       { get; set; } = string.Empty;
    public string  MetricUnit       { get; set; } = string.Empty;
    /// <summary>Enum8 in ClickHouse: GAUGE=1, SUM=2, HISTOGRAM=3.</summary>
    public string  MetricKind       { get; set; } = "GAUGE";
    /// <summary>Enum8 in ClickHouse: UNSPECIFIED=0, DELTA=1, CUMULATIVE=2.</summary>
    public string  AggregationTemporality { get; set; } = "UNSPECIFIED";
    public bool    IsMonotonic      { get; set; }

    /// <summary>Scalar value when <see cref="MetricKind"/> is GAUGE or SUM (double path).</summary>
    public double? ValueDouble      { get; set; }
    /// <summary>Scalar value when the source emitted an integer point (Counter&lt;long&gt;).</summary>
    public long?   ValueInt         { get; set; }

    public ulong?  HistogramCount   { get; set; }
    public double? HistogramSum     { get; set; }
    public double? HistogramMin     { get; set; }
    public double? HistogramMax     { get; set; }
    public ulong[] HistogramBucketCounts { get; set; } = Array.Empty<ulong>();
    public double[] HistogramBucketBounds { get; set; } = Array.Empty<double>();

    public string[] AttrsKeys       { get; set; } = Array.Empty<string>();
    public string[] AttrsValues     { get; set; } = Array.Empty<string>();

    public string  AgentVersion     { get; set; } = string.Empty;
}
