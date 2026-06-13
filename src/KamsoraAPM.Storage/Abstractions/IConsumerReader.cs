// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>
/// Read-side queries for the M6 consumer + status rollups, scoped per
/// tenant. Powers the Consumers + Errors dashboards. Backed by the
/// <c>consumer_hourly_rollup</c> + <c>status_hourly_rollup</c>
/// AggregatingMergeTree tables, NOT the raw <c>spans</c> table — keeps the
/// "who's calling us, by how much" query O(rollup) instead of O(spans).
/// </summary>
public interface IConsumerReader
{
    /// <summary>One row per consumer that called any route in the window.</summary>
    Task<IReadOnlyList<ConsumerSummary>> ListConsumersAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc,
        string? serviceName, int limit, CancellationToken ct);

    /// <summary>Per-bucket traffic for a single consumer (all routes).</summary>
    Task<IReadOnlyList<ConsumerTimeseriesPoint>> GetConsumerTimeseriesAsync(
        Guid tenantId, string consumerId, DateTime fromUtc, DateTime toUtc,
        int bucketSeconds, CancellationToken ct);

    /// <summary>Top routes for a single consumer in the window, sorted by request count desc.</summary>
    Task<IReadOnlyList<ConsumerRouteSummary>> GetConsumerTopRoutesAsync(
        Guid tenantId, string consumerId, DateTime fromUtc, DateTime toUtc,
        int limit, CancellationToken ct);

    /// <summary>
    /// Same as <see cref="GetConsumerTopRoutesAsync"/> but every row carries an
    /// inline <c>sparkline</c> — per-hour request counts across the window —
    /// for the dashboard table's mini-chart column. One query, no per-row fanout.
    /// </summary>
    Task<IReadOnlyList<ConsumerRouteWithSparkline>> GetConsumerRoutesWithSparklineAsync(
        Guid tenantId, string consumerId, DateTime fromUtc, DateTime toUtc,
        int limit, CancellationToken ct);
}

/// <summary>Read-side queries for the M6 status-code rollup — powers the Errors dashboard.</summary>
public interface IErrorsReader
{
    /// <summary>Per-status-code totals + latency, grouped by route. Useful for "where are the 4xxs?" drilldowns.</summary>
    Task<IReadOnlyList<RouteStatusSummary>> GetRouteStatusBreakdownAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc,
        string? serviceName, int limit, CancellationToken ct);

    /// <summary>Status-code distribution across the whole window for a single route.</summary>
    Task<IReadOnlyList<StatusCodeBucket>> GetStatusBreakdownForRouteAsync(
        Guid tenantId, string serviceName, string httpRoute,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

public sealed record ConsumerSummary(
    string   ConsumerId,
    long     RequestCount,
    long     ErrorCount,
    double   ErrorRate,           // 0..1
    long     ClientErrorCount,    // 4xx
    long     ServerErrorCount,    // 5xx
    double   LatencyP50Ms,
    double   LatencyP90Ms,
    double   LatencyP99Ms,
    int      DistinctRoutes,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc);

public sealed record ConsumerTimeseriesPoint(
    DateTime BucketStartUtc,
    long     RequestCount,
    long     ErrorCount,
    long     ClientErrorCount,
    long     ServerErrorCount,
    double   LatencyP50Ms,
    double   LatencyP90Ms,
    double   LatencyP99Ms);

public sealed record ConsumerRouteSummary(
    string   ServiceName,
    string   HttpRoute,
    long     RequestCount,
    long     ErrorCount,
    double   ErrorRate,
    double   LatencyP50Ms,
    double   LatencyP90Ms,
    double   LatencyP99Ms);

/// <summary>
/// Route summary + per-hour request-count timeseries (the "sparkline").
/// Sparkline length always equals the requested number of hourly buckets so
/// the dashboard can render fixed-width SVGs without per-row width math.
/// </summary>
public sealed record ConsumerRouteWithSparkline(
    string         ServiceName,
    string         HttpRoute,
    long           RequestCount,
    long           ErrorCount,
    double         ErrorRate,
    double         LatencyP50Ms,
    double         LatencyP99Ms,
    IReadOnlyList<long> Sparkline);

public sealed record RouteStatusSummary(
    string   ServiceName,
    string   HttpRoute,
    long     RequestCount,
    long     Status2xx,
    long     Status3xx,
    long     Status4xx,
    long     Status5xx,
    double   ErrorRate,             // (4xx + 5xx) / total
    double   LatencyP50Ms,
    double   LatencyP99Ms);

public sealed record StatusCodeBucket(
    int      HttpStatusCode,
    long     RequestCount,
    double   LatencyP50Ms,
    double   LatencyP99Ms);
