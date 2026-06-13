// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KamsoraAPM.Dashboard.Api.Alerting;

/// <summary>
/// Lazy in-memory cache of <c>mastertenants.tenant_slug</c> by tenant UUID.
/// Refresh on miss; never invalidate - slugs are stable in practice.
/// </summary>
public sealed class TenantSlugLookup : ITenantSlugLookup
{
    private readonly PostgresOptions _options;
    private readonly ConcurrentDictionary<Guid, string> _cache = new();

    public TenantSlugLookup(IOptions<PostgresOptions> options) => _options = options.Value;

    public string? LookupSlug(Guid tenantId)
    {
        if (_cache.TryGetValue(tenantId, out var cached)) return cached;
        try
        {
            using var conn = new NpgsqlConnection(_options.ConnectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "SELECT tenant_slug FROM public.mastertenants WHERE systenantuuid = @uuid", conn);
            cmd.Parameters.AddWithValue("uuid", tenantId.ToString());
            var slug = cmd.ExecuteScalar() as string;
            if (slug is not null) _cache[tenantId] = slug;
            return slug;
        }
        catch
        {
            return null;
        }
    }
}
