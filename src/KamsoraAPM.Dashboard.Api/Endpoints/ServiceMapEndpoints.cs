// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Abstractions;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// Service-dependency graph for the Service Map page:
/// <c>GET /api/v1/services/map?fromUtc=…&amp;toUtc=…</c>
/// </summary>
public static class ServiceMapEndpoints
{
    public static IEndpointRouteBuilder MapServiceMapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/services/map", async (
            HttpContext http,
            DateTime fromUtc, DateTime toUtc,
            IServiceMapReader reader, CancellationToken ct) =>
        {
            var claim = http.User.FindFirst(KamsoraClaimTypes.TenantId);
            if (claim is null || !Guid.TryParse(claim.Value, out var tenantId))
                return Results.Unauthorized();

            var map = await reader.GetServiceMapAsync(tenantId, fromUtc, toUtc, ct).ConfigureAwait(false);
            return Results.Ok(map);
        }).RequireAuthorization();

        return app;
    }
}
