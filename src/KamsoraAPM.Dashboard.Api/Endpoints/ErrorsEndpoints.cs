// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Abstractions;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// M6 4xx / 5xx breakdown endpoints. Backed by <see cref="IErrorsReader"/>
/// over the <c>status_hourly_rollup</c> AggregatingMergeTree.
/// </summary>
public static class ErrorsEndpoints
{
    public static IEndpointRouteBuilder MapErrorsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/errors").RequireAuthorization();

        // Worst-offender list: routes sorted by 4xx+5xx desc.
        group.MapGet("/routes", async (
            HttpContext http,
            DateTime fromUtc,
            DateTime toUtc,
            string? serviceName,
            int? limit,
            IErrorsReader reader,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var list = await reader.GetRouteStatusBreakdownAsync(
                tenantId, fromUtc, toUtc, NullIfEmpty(serviceName), limit ?? 50, ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        // Drilldown: status-code distribution for one route.
        group.MapGet("/routes/{serviceName}/{httpRoute}", async (
            HttpContext http,
            string serviceName,
            string httpRoute,
            DateTime fromUtc,
            DateTime toUtc,
            IErrorsReader reader,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var list = await reader.GetStatusBreakdownForRouteAsync(
                tenantId,
                Uri.UnescapeDataString(serviceName),
                Uri.UnescapeDataString(httpRoute),
                fromUtc, toUtc, ct).ConfigureAwait(false);
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
