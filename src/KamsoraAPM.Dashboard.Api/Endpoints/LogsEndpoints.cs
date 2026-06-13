// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Abstractions;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// M8 logs ingestion read-side. Three endpoints:
///   <list type="bullet">
///     <item><c>GET /api/v1/logs</c> — paginated list with filters.</item>
///     <item><c>GET /api/v1/logs/by-trace/{traceId}</c> — logs correlated to one trace.</item>
///     <item><c>GET /api/v1/logs/timeseries</c> — per-minute volume stack by severity.</item>
///   </list>
/// </summary>
public static class LogsEndpoints
{
    public static IEndpointRouteBuilder MapLogsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/logs").RequireAuthorization();

        group.MapGet("/", async (
            HttpContext http,
            DateTime? fromUtc, DateTime? toUtc,
            string? serviceName, byte? minSeverity, string? body, string? traceId,
            int? limit, ulong? cursorTimeUnixNano,
            ILogReader reader, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var list = await reader.ListLogsAsync(
                tid, fromUtc, toUtc, NullIfEmpty(serviceName), minSeverity,
                NullIfEmpty(body), NullIfEmpty(traceId),
                limit ?? 100, cursorTimeUnixNano ?? 0UL, ct).ConfigureAwait(false);
            var nextCursor = list.Count == 0 ? (ulong?)null
                : (ulong)new DateTimeOffset(list[^1].TimestampUtc).ToUnixTimeMilliseconds() * 1_000_000UL;
            return Results.Ok(new { items = list, nextCursorTimeUnixNano = nextCursor });
        });

        group.MapGet("/by-trace/{traceId}", async (
            HttpContext http, string traceId,
            ILogReader reader, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var list = await reader.GetLogsForTraceAsync(tid, traceId, ct).ConfigureAwait(false);
            return Results.Ok(list);
        });

        group.MapGet("/timeseries", async (
            HttpContext http, DateTime fromUtc, DateTime toUtc,
            string? serviceName, int? bucketSeconds,
            ILogReader reader, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var list = await reader.GetLogVolumeTimeseriesAsync(
                tid, fromUtc, toUtc, NullIfEmpty(serviceName), bucketSeconds ?? 60, ct).ConfigureAwait(false);
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
