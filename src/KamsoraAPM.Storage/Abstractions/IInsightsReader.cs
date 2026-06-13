// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>
/// Aggregations + per-trace lookups powering the Dashboard.Api.
/// Implementations query the time-series store (ClickHouse) with tenant
/// pruning as the first filter.
/// </summary>
public interface IInsightsReader
{
    Task<OverviewSnapshot> GetOverviewAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    Task<IReadOnlyList<ServiceSummary>> ListServicesAsync(Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    Task<IReadOnlyList<TimeseriesPoint>> GetLatencyTimeseriesAsync(
        Guid tenantId, string? serviceName, DateTime fromUtc, DateTime toUtc,
        int bucketSeconds, CancellationToken ct);

    Task<IReadOnlyList<Models.SpanRow>> GetTraceAsync(Guid tenantId, string traceIdHex, CancellationToken ct);

    Task<IReadOnlyList<TopRoute>> GetTopRoutesAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct);

    Task<DatabaseOverview> GetDatabaseOverviewAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    Task<IReadOnlyList<TopQuery>> GetTopQueriesAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct);

    Task<IReadOnlyList<DbSystemBreakdown>> GetDbSystemBreakdownAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>
    /// Per-service request/error counts split into <paramref name="buckets"/> equal
    /// time slices — one query for every service, powering table sparklines.
    /// Every returned array has exactly <paramref name="buckets"/> entries.
    /// </summary>
    Task<IReadOnlyList<EntitySparkline>> GetServiceSparklinesAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, int buckets, CancellationToken ct);

    /// <summary>
    /// Same as <see cref="GetServiceSparklinesAsync"/> but keyed by consumer id,
    /// restricted to the top <paramref name="limit"/> consumers by request count.
    /// </summary>
    Task<IReadOnlyList<EntitySparkline>> GetConsumerSparklinesAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, int buckets, int limit, CancellationToken ct);

    /// <summary>
    /// Everything the route drill-down drawer shows: summary percentiles,
    /// request timeseries, and a log2 latency histogram. Null when the route
    /// has no traffic in the window.
    /// </summary>
    Task<RouteDetail?> GetRouteDetailAsync(
        Guid tenantId, string serviceName, string httpRoute,
        DateTime fromUtc, DateTime toUtc, int bucketSeconds, CancellationToken ct);
}

public sealed record OverviewSnapshot(
    long   TotalSpans,
    long   ErrorSpans,
    double ErrorRate,
    double LatencyP50Ms,
    double LatencyP90Ms,
    double LatencyP99Ms,
    int    DistinctServices);

public sealed record ServiceSummary(
    string ServiceName,
    string ServiceVersion,
    long   SpanCount,
    long   ErrorCount,
    double ErrorRate,
    double LatencyP50Ms,
    double LatencyP90Ms,
    double LatencyP99Ms,
    DateTime LastSeenUtc);

public sealed record TimeseriesPoint(
    DateTime BucketStartUtc,
    long     Count,
    long     ErrorCount,
    double   LatencyP50Ms,
    double   LatencyP90Ms,
    double   LatencyP99Ms);

public sealed record TopRoute(
    string ServiceName,
    string SpanName,
    string HttpMethod,
    string HttpRoute,
    long   Count,
    long   ErrorCount,
    double LatencyP50Ms,
    double LatencyP90Ms,
    double LatencyP99Ms);

public sealed record DatabaseOverview(
    long   TotalQueries,
    long   ErrorQueries,
    double ErrorRate,
    double LatencyP50Ms,
    double LatencyP90Ms,
    double LatencyP99Ms,
    double TotalDbTimeMs,
    int    DistinctSystems);

public sealed record TopQuery(
    string DbSystem,
    string Statement,
    long   Count,
    long   ErrorCount,
    double LatencyP50Ms,
    double LatencyP90Ms,
    double LatencyP99Ms,
    double TotalDbTimeMs);

public sealed record DbSystemBreakdown(
    string DbSystem,
    long   Count,
    double LatencyP50Ms,
    double LatencyP99Ms);

/// <summary>
/// Fixed-length per-bucket counts for one keyed entity (service, consumer).
/// <c>Counts</c> and <c>Errors</c> always have the same, caller-chosen length
/// so the dashboard renders fixed-width sparklines without per-row math.
/// </summary>
public sealed record EntitySparkline(
    string Key,
    IReadOnlyList<long> Counts,
    IReadOnlyList<long> Errors);

/// <summary>Latency histogram bucket: requests whose duration fell in [FromMs, ToMs).</summary>
public sealed record HistogramBucket(
    double FromMs,
    double ToMs,
    long   Count);

public sealed record RouteDetail(
    string ServiceName,
    string HttpMethod,
    string HttpRoute,
    long   Count,
    long   ErrorCount,
    double ErrorRate,
    double RequestsPerMinute,
    double LatencyP50Ms,
    double LatencyP75Ms,
    double LatencyP95Ms,
    double LatencyP99Ms,
    IReadOnlyList<TimeseriesPoint> Timeseries,
    IReadOnlyList<HistogramBucket> Histogram);
