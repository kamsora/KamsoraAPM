// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>
/// Builds the service-dependency graph for the dashboard's Service Map page.
/// Nodes are services, databases, and external HTTP hosts; edges carry call
/// volume, error counts, and average latency over the queried window.
/// </summary>
public interface IServiceMapReader
{
    Task<ServiceMapResult> GetServiceMapAsync(
        Guid tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

public sealed record ServiceMapResult(
    IReadOnlyList<ServiceMapNode> Nodes,
    IReadOnlyList<ServiceMapEdge> Edges);

/// <summary>
/// One graph node. <c>Id</c> is stable across renders — "svc:checkout",
/// "db:postgresql", "ext:api.stripe.com". <c>Kind</c> is one of
/// <c>service</c> | <c>database</c> | <c>external</c>.
/// </summary>
public sealed record ServiceMapNode(
    string Id,
    string Label,
    string Kind,
    long   CallCount,
    long   ErrorCount,
    double LatencyP50Ms);

public sealed record ServiceMapEdge(
    string SourceId,
    string TargetId,
    long   CallCount,
    long   ErrorCount,
    double AvgLatencyMs);
