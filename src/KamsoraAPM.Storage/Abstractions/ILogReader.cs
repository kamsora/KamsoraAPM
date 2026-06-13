// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>Read-side queries over <c>kamsora_apm.logs</c> for the LogsPage.</summary>
public interface ILogReader
{
    /// <summary>
    /// Most-recent-first log list with optional filters. <paramref name="bodySearch"/>
    /// uses case-insensitive substring match via ClickHouse's <c>positionCaseInsensitive</c>.
    /// </summary>
    Task<IReadOnlyList<LogRowDto>> ListLogsAsync(
        Guid tenantId, DateTime? fromUtc, DateTime? toUtc,
        string? serviceName, byte? minSeverity, string? bodySearch, string? traceIdHex,
        int limit, ulong cursorTimeUnixNano, CancellationToken ct);

    /// <summary>All logs that share a trace_id, ascending by timestamp. For the trace drawer's "linked logs" panel.</summary>
    Task<IReadOnlyList<LogRowDto>> GetLogsForTraceAsync(
        Guid tenantId, string traceIdHex, CancellationToken ct);

    /// <summary>Per-minute log volume bucketed by severity_text for the LogsPage chart.</summary>
    Task<IReadOnlyList<LogVolumePoint>> GetLogVolumeTimeseriesAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc,
        string? serviceName, int bucketSeconds, CancellationToken ct);
}

/// <summary>Read-side queries over <c>kamsora_apm.metric_points</c> for the MetricsPage.</summary>
public interface IMetricReader
{
    /// <summary>Distinct metric names with their kind + unit + last-seen timestamp.</summary>
    Task<IReadOnlyList<MetricCatalogEntry>> ListMetricsAsync(
        Guid tenantId, DateTime? fromUtc, DateTime? toUtc, string? serviceName,
        CancellationToken ct);

    /// <summary>Time series for one metric (one line per attribute combination).</summary>
    Task<IReadOnlyList<MetricSeriesPoint>> GetMetricSeriesAsync(
        Guid tenantId, string metricName, DateTime fromUtc, DateTime toUtc,
        string? serviceName, int bucketSeconds, CancellationToken ct);
}

public sealed record LogRowDto(
    DateTime  TimestampUtc,
    string    ServiceName,
    int       SeverityNumber,
    string    SeverityText,
    string    Body,
    string    TraceIdHex,
    string    SpanIdHex,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record LogVolumePoint(
    DateTime  BucketStartUtc,
    string    SeverityText,
    long      LogCount);

public sealed record MetricCatalogEntry(
    string    MetricName,
    string    MetricKind,
    string    MetricUnit,
    string    ServiceName,
    DateTime  LastSeenUtc,
    long      PointCount);

public sealed record MetricSeriesPoint(
    DateTime  BucketStartUtc,
    string    SeriesKey,        // serialised attrs for legend grouping
    double    ValueLast,
    double    ValueMax,
    double    ValueMin);
