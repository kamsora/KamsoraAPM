// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Abstractions;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// Host telemetry endpoints powering the React "Hosts" page. All routes are
/// tenant-scoped via the <c>kamsora_tenant</c> JWT claim and read from
/// ClickHouse <c>kamsora_apm.host_cpu_memory</c>.
/// </summary>
public static class HostsEndpoints
{
    public static IEndpointRouteBuilder MapHostsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization();

        // GET /api/v1/hosts
        group.MapGet("/hosts", async (
            HttpContext http,
            IHostReader reader,
            CancellationToken ct,
            DateTime? fromUtc = null,
            DateTime? toUtc   = null) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var hosts = await reader.ListHostsAsync(tenantId, from, to, ct).ConfigureAwait(false);
            return Results.Ok(hosts);
        });

        // GET /api/v1/hosts/{hostId}/utilization
        group.MapGet("/hosts/{hostId}/utilization", async (
            HttpContext http,
            IHostReader reader,
            string hostId,
            CancellationToken ct,
            DateTime? fromUtc       = null,
            DateTime? toUtc         = null,
            int       bucketSeconds = 60) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var points = await reader.GetUtilizationTimeseriesAsync(
                tenantId, hostId, from, to, bucketSeconds, ct).ConfigureAwait(false);
            return Results.Ok(points);
        });

        // GET /api/v1/hosts/{hostId}/disks
        group.MapGet("/hosts/{hostId}/disks", async (
            HttpContext http,
            IHostReader reader,
            string hostId,
            CancellationToken ct,
            DateTime? fromUtc       = null,
            DateTime? toUtc         = null,
            int       bucketSeconds = 60) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var points = await reader.GetDiskTimeseriesAsync(
                tenantId, hostId, from, to, bucketSeconds, ct).ConfigureAwait(false);
            return Results.Ok(points);
        });

        // GET /api/v1/hosts/{hostId}/networks
        group.MapGet("/hosts/{hostId}/networks", async (
            HttpContext http,
            IHostReader reader,
            string hostId,
            CancellationToken ct,
            DateTime? fromUtc       = null,
            DateTime? toUtc         = null,
            int       bucketSeconds = 60) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var (from, to) = ResolveRange(fromUtc, toUtc);
            var points = await reader.GetNetworkTimeseriesAsync(
                tenantId, hostId, from, to, bucketSeconds, ct).ConfigureAwait(false);
            return Results.Ok(points);
        });

        // GET /api/v1/hosts/{hostId}/processes
        group.MapGet("/hosts/{hostId}/processes", async (
            HttpContext http,
            IHostReader reader,
            string hostId,
            CancellationToken ct,
            int limit = 50) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            var processes = await reader.GetTopProcessesAsync(
                tenantId, hostId, limit, ct).ConfigureAwait(false);
            return Results.Ok(processes);
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
