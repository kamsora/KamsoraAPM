// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>Read-side counterpart of <see cref="ISpanWriter"/> used by the Dashboard.Api.</summary>
public interface ISpanReader
{
    /// <summary>
    /// List recent spans for a tenant, optionally filtered by service name.
    /// Returns most-recent-first. Pagination via <paramref name="limit"/> +
    /// <paramref name="cursorTimeUnixNano"/>.
    /// </summary>
    Task<IReadOnlyList<SpanRow>> ListRecentAsync(
        Guid tenantId,
        string? serviceName,
        string? spanKind,
        DateTime? fromUtc,
        DateTime? toUtc,
        int limit,
        ulong cursorTimeUnixNano,
        CancellationToken cancellationToken)
        => ListRecentAsync(tenantId, serviceName, spanKind, fromUtc, toUtc,
            consumerId: null, httpRoute: null, errorsOnly: false,
            limit, cursorTimeUnixNano, cancellationToken);

    /// <summary>
    /// Extended overload with M6 drill-through filters. <paramref name="consumerId"/>
    /// matches <c>spans.consumer_id</c>; the sentinel value <c>"(anonymous)"</c>
    /// is mapped to the empty string before the query runs. <paramref name="httpRoute"/>
    /// matches the hoisted <c>http_route</c> column. <paramref name="errorsOnly"/>
    /// restricts to <c>status_code = 'ERROR' OR http_status_code &gt;= 400</c>.
    /// </summary>
    Task<IReadOnlyList<SpanRow>> ListRecentAsync(
        Guid tenantId,
        string? serviceName,
        string? spanKind,
        DateTime? fromUtc,
        DateTime? toUtc,
        string? consumerId,
        string? httpRoute,
        bool errorsOnly,
        int limit,
        ulong cursorTimeUnixNano,
        CancellationToken cancellationToken);
}
