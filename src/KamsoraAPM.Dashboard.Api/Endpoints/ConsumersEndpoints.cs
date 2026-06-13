// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Abstractions;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// M6 consumer analytics endpoints. Backed by <see cref="IConsumerReader"/>
/// over the <c>consumer_hourly_rollup</c> AggregatingMergeTree.
/// </summary>
public static class ConsumersEndpoints
{
    public static IEndpointRouteBuilder MapConsumersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/consumers").RequireAuthorization();

        group.MapGet("/", async (
            HttpContext http,
            DateTime fromUtc,
            DateTime toUtc,
            string? serviceName,
            int? limit,
            IConsumerReader reader,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var list = await reader.ListConsumersAsync(
                tenantId, fromUtc, toUtc, NullIfEmpty(serviceName), limit ?? 100, ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        group.MapGet("/{consumerId}/timeseries", async (
            HttpContext http,
            string consumerId,
            DateTime fromUtc,
            DateTime toUtc,
            int? bucketSeconds,
            IConsumerReader reader,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var list = await reader.GetConsumerTimeseriesAsync(
                tenantId, Uri.UnescapeDataString(consumerId), fromUtc, toUtc, bucketSeconds ?? 3600, ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        group.MapGet("/{consumerId}/routes", async (
            HttpContext http,
            string consumerId,
            DateTime fromUtc,
            DateTime toUtc,
            int? limit,
            IConsumerReader reader,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var list = await reader.GetConsumerTopRoutesAsync(
                tenantId, Uri.UnescapeDataString(consumerId), fromUtc, toUtc, limit ?? 20, ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        // Routes + inline per-hour sparkline. One round-trip, one chart per row.
        group.MapGet("/{consumerId}/routes-detailed", async (
            HttpContext http,
            string consumerId,
            DateTime fromUtc,
            DateTime toUtc,
            int? limit,
            IConsumerReader reader,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var list = await reader.GetConsumerRoutesWithSparklineAsync(
                tenantId, Uri.UnescapeDataString(consumerId), fromUtc, toUtc, limit ?? 20, ct).ConfigureAwait(false);
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
