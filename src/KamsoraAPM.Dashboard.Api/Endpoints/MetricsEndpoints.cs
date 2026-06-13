// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Abstractions;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// M8 metrics read-side. Two endpoints:
///   <list type="bullet">
///     <item><c>GET /api/v1/metrics</c> — metric catalog (distinct names + kind + last seen).</item>
///     <item><c>GET /api/v1/metrics/{name}/series</c> — per-bucket value series.</item>
///   </list>
/// </summary>
public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/metrics").RequireAuthorization();

        group.MapGet("/", async (
            HttpContext http, DateTime? fromUtc, DateTime? toUtc, string? serviceName,
            IMetricReader reader, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var list = await reader.ListMetricsAsync(tid, fromUtc, toUtc, NullIfEmpty(serviceName), ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        group.MapGet("/{metricName}/series", async (
            HttpContext http, string metricName,
            DateTime fromUtc, DateTime toUtc,
            string? serviceName, int? bucketSeconds,
            IMetricReader reader, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var list = await reader.GetMetricSeriesAsync(
                tid, Uri.UnescapeDataString(metricName), fromUtc, toUtc,
                NullIfEmpty(serviceName), bucketSeconds ?? 60, ct).ConfigureAwait(false);
            return Results.Ok(list);
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

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
