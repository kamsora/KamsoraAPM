// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// <c>GET /api/v1/traces</c> — tenant-scoped span listing for the dashboard
/// Trace Explorer view. Pagination by descending <c>start_time_unix_ns</c>
/// cursor; <c>limit</c> capped server-side at 1000.
/// </summary>
public static class TracesEndpoints
{
    public static IEndpointRouteBuilder MapTraceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/traces").RequireAuthorization();

        group.MapGet(string.Empty, async (
            HttpContext         http,
            ISpanReader         reader,
            CancellationToken   ct,
            string?             service     = null,
            string?             kind        = null,
            DateTime?           fromUtc     = null,
            DateTime?           toUtc       = null,
            string?             consumerId  = null,
            string?             route       = null,
            bool                errorsOnly  = false,
            int                 limit       = 50,
            ulong               cursorNs    = 0UL) =>
        {
            if (!TryGetTenant(http, out var tenantId))
                return Results.Unauthorized();

            var rows = await reader.ListRecentAsync(
                tenantId, service, kind, fromUtc, toUtc,
                consumerId, route, errorsOnly,
                limit, cursorNs, ct).ConfigureAwait(false);
            var dto  = rows.Select(SpanRowDto.FromRow).ToArray();
            var next = dto.Length == 0 ? (ulong?)null : dto[^1].StartTimeUnixNano;
            return Results.Ok(new TraceListResponse(dto, next));
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
}

/// <summary>JSON-friendly projection of <see cref="SpanRow"/>.</summary>
public sealed record SpanRowDto(
    string TraceId,
    string SpanId,
    string ParentSpanId,
    ulong  StartTimeUnixNano,
    ulong  EndTimeUnixNano,
    ulong  DurationNanos,
    string ServiceName,
    string ServiceVersion,
    string SpanName,
    string SpanKind,
    string StatusCode,
    string StatusMessage,
    string HttpMethod,
    int    HttpStatusCode,
    string HttpRoute,
    string HttpUrl,
    string HttpClientIp,
    string ConsumerId,
    string DbSystem,
    string DbStatement,
    ulong  DbDurationNs,
    // Attributes: all non-hoisted span attributes as a flat dictionary.
    // Events: span events (e.g. exceptions) in chronological order.
    Dictionary<string, string> Attributes,
    SpanEventDto[] Events)
{
    public static SpanRowDto FromRow(SpanRow r)
    {
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (r.AttrsKeys.Length == r.AttrsValues.Length)
        {
            for (int i = 0; i < r.AttrsKeys.Length; i++)
            {
                attrs[r.AttrsKeys[i]] = r.AttrsValues[i];
            }
        }

        var events = new SpanEventDto[r.EventNames.Length];
        for (int i = 0; i < r.EventNames.Length; i++)
        {
            events[i] = new SpanEventDto(
                r.EventNames[i],
                i < r.EventTimesUnixNs.Length ? r.EventTimesUnixNs[i] : 0UL,
                i < r.EventAttrsJson.Length ? r.EventAttrsJson[i] : "{}");
        }

        return new SpanRowDto(
            TraceId:           Convert.ToHexString(r.TraceId).ToLowerInvariant(),
            SpanId:            Convert.ToHexString(r.SpanId).ToLowerInvariant(),
            ParentSpanId:      r.ParentSpanId.Length == 0 ? string.Empty : Convert.ToHexString(r.ParentSpanId).ToLowerInvariant(),
            StartTimeUnixNano: r.StartTimeUnixNano,
            EndTimeUnixNano:   r.EndTimeUnixNano,
            DurationNanos:     r.EndTimeUnixNano > r.StartTimeUnixNano ? r.EndTimeUnixNano - r.StartTimeUnixNano : 0UL,
            ServiceName:       r.ServiceName,
            ServiceVersion:    r.ServiceVersion,
            SpanName:          r.SpanName,
            SpanKind:          r.SpanKind,
            StatusCode:        r.StatusCode,
            StatusMessage:     r.StatusMessage,
            HttpMethod:        r.HttpMethod,
            HttpStatusCode:    r.HttpStatusCode,
            HttpRoute:         r.HttpRoute,
            HttpUrl:           r.HttpUrl,
            HttpClientIp:      r.HttpClientIp,
            ConsumerId:        r.ConsumerId,
            DbSystem:          r.DbSystem,
            DbStatement:       r.DbStatement,
            DbDurationNs:      r.DbDurationNs,
            Attributes:        attrs,
            Events:            events);
    }
}

public sealed record SpanEventDto(string Name, ulong TimeUnixNano, string AttributesJson);

public sealed record TraceListResponse(SpanRowDto[] Items, ulong? NextCursorNs);
