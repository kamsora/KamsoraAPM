// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Abstractions;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// Read-only aggregation endpoints that power the React dashboard.
/// All routes are tenant-scoped via the <c>kamsora_tenant</c> JWT claim.
/// </summary>
public static class InsightsEndpoints
{
    public static IEndpointRouteBuilder MapInsightsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization();

        // GET /api/v1/overview
        group.MapGet("/overview", async (
            HttpContext http,
            IInsightsReader reader,
            CancellationToken ct,
            DateTime? fromUtc = null,
            DateTime? toUtc   = null) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var snapshot   = await reader.GetOverviewAsync(tenantId, from, to, ct).ConfigureAwait(false);
            return Results.Ok(snapshot);
        });

        // GET /api/v1/services
        group.MapGet("/services", async (
            HttpContext http,
            IInsightsReader reader,
            CancellationToken ct,
            DateTime? fromUtc = null,
            DateTime? toUtc   = null) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var services   = await reader.ListServicesAsync(tenantId, from, to, ct).ConfigureAwait(false);
            return Results.Ok(services);
        });

        // GET /api/v1/timeseries/latency
        group.MapGet("/timeseries/latency", async (
            HttpContext http,
            IInsightsReader reader,
            CancellationToken ct,
            string?  service       = null,
            DateTime? fromUtc      = null,
            DateTime? toUtc        = null,
            int      bucketSeconds = 60) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var points = await reader.GetLatencyTimeseriesAsync(tenantId, service, from, to, bucketSeconds, ct).ConfigureAwait(false);
            return Results.Ok(points);
        });

        // GET /api/v1/top-routes
        group.MapGet("/top-routes", async (
            HttpContext http,
            IInsightsReader reader,
            CancellationToken ct,
            DateTime? fromUtc = null,
            DateTime? toUtc   = null,
            int       limit   = 20) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var routes = await reader.GetTopRoutesAsync(tenantId, from, to, limit, ct).ConfigureAwait(false);
            return Results.Ok(routes);
        });

        // GET /api/v1/database/overview
        group.MapGet("/database/overview", async (
            HttpContext http,
            IInsightsReader reader,
            CancellationToken ct,
            DateTime? fromUtc = null,
            DateTime? toUtc   = null) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var snapshot   = await reader.GetDatabaseOverviewAsync(tenantId, from, to, ct).ConfigureAwait(false);
            return Results.Ok(snapshot);
        });

        // GET /api/v1/database/top-queries
        group.MapGet("/database/top-queries", async (
            HttpContext http,
            IInsightsReader reader,
            CancellationToken ct,
            DateTime? fromUtc = null,
            DateTime? toUtc   = null,
            int       limit   = 15) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var queries    = await reader.GetTopQueriesAsync(tenantId, from, to, limit, ct).ConfigureAwait(false);
            return Results.Ok(queries);
        });

        // GET /api/v1/database/systems
        group.MapGet("/database/systems", async (
            HttpContext http,
            IInsightsReader reader,
            CancellationToken ct,
            DateTime? fromUtc = null,
            DateTime? toUtc   = null) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var systems    = await reader.GetDbSystemBreakdownAsync(tenantId, from, to, ct).ConfigureAwait(false);
            return Results.Ok(systems);
        });

        // GET /api/v1/services/sparklines - per-service mini-timeseries for table sparklines.
        group.MapGet("/services/sparklines", async (
            HttpContext http,
            IInsightsReader reader,
            CancellationToken ct,
            DateTime? fromUtc = null,
            DateTime? toUtc   = null,
            int       buckets = 30) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var sparks = await reader.GetServiceSparklinesAsync(tenantId, from, to, buckets, ct).ConfigureAwait(false);
            return Results.Ok(sparks);
        });

        // GET /api/v1/consumers/sparklines - per-consumer mini-timeseries (top N by volume).
        group.MapGet("/consumers/sparklines", async (
            HttpContext http,
            IInsightsReader reader,
            CancellationToken ct,
            DateTime? fromUtc = null,
            DateTime? toUtc   = null,
            int       buckets = 30,
            int       limit   = 200) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var sparks = await reader.GetConsumerSparklinesAsync(tenantId, from, to, buckets, limit, ct).ConfigureAwait(false);
            return Results.Ok(sparks);
        });

        // GET /api/v1/routes/detail - drill-down payload for one route:
        // summary percentiles + request timeseries + log2 latency histogram.
        group.MapGet("/routes/detail", async (
            HttpContext http,
            IInsightsReader reader,
            CancellationToken ct,
            string    service,
            string    route,
            DateTime? fromUtc       = null,
            DateTime? toUtc         = null,
            int       bucketSeconds = 60) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var detail = await reader.GetRouteDetailAsync(tenantId, service, route, from, to, bucketSeconds, ct).ConfigureAwait(false);
            return detail is null
                ? Results.NotFound(new { service, route })
                : Results.Ok(detail);
        });

        // GET /api/v1/traces/{traceId}
        group.MapGet("/traces/{traceId}", async (
            HttpContext http,
            IInsightsReader reader,
            string traceId,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var rows = await reader.GetTraceAsync(tenantId, traceId, ct).ConfigureAwait(false);
            if (rows.Count == 0) return Results.NotFound(new { traceId });
            return Results.Ok(rows.Select(SpanRowDto.FromRow).ToArray());
        });

        return app;
    }

    private static bool TryGetTenant(HttpContext http, out Guid tenantId)
    {
        var claim = http.User.FindFirst(KamsoraClaimTypes.TenantId);
        if (claim is null || !Guid.TryParse(claim.Value, out tenantId))
        {
            tenantId = Guid.Empty;
            return false;
        }
        return true;
    }

    private static (DateTime from, DateTime to) ResolveRange(DateTime? fromUtc, DateTime? toUtc)
    {
        var to   = (toUtc   ?? DateTime.UtcNow).ToUniversalTime();
        var from = (fromUtc ?? to.AddHours(-1)).ToUniversalTime();
        return (from, to);
    }
}
