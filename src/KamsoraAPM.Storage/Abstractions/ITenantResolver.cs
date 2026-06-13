// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>
/// Verifies a (tenant UUID, cleartext API key) pair and returns a populated
/// <see cref="ResolvedTenant"/> when valid, or <c>null</c> when not.
///
/// Used by the Collector gRPC auth interceptor on every ingestion call.
/// Implementations are expected to cache positive results in-memory for the
/// duration of the API key's TTL to avoid hammering PostgreSQL.
/// </summary>
public interface ITenantResolver
{
    Task<ResolvedTenant?> ResolveAsync(string tenantId, string apiKey, CancellationToken cancellationToken);
}

/// <summary>The resolved identity of a tenant making an ingestion call.</summary>
public sealed record ResolvedTenant(
    Guid   TenantId,
    string TenantSlug,
    string ApiKeyId,
    string Scopes);
