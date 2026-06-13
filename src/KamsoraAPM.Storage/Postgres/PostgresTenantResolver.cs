// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KamsoraAPM.Storage.Postgres;

/// <summary>
/// Looks up an API key in <c>masterapi_keys</c> via the Kamsora-pattern
/// stored function <c>fn_api_select_masterapi_keys</c>, BCrypt-verifies the
/// presented secret, and caches positive results.
///
/// M1: simple in-memory <see cref="ConcurrentDictionary{TKey,TValue}"/> cache
/// with a 5-minute TTL. M5 swaps this for a proper LRU with size cap and
/// per-tenant rate-limit tracking.
/// </summary>
public sealed class PostgresTenantResolver : ITenantResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly PostgresOptions _options;
    private readonly ILogger<PostgresTenantResolver> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public PostgresTenantResolver(IOptions<PostgresOptions> options, ILogger<PostgresTenantResolver> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<ResolvedTenant?> ResolveAsync(string tenantId, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var cacheKey = CacheKey(tenantId, apiKey);
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Tenant;
        }

        var resolved = await ResolveFromPostgresAsync(tenantId, apiKey, cancellationToken).ConfigureAwait(false);
        if (resolved is not null)
        {
            _cache[cacheKey] = new CacheEntry(resolved, DateTime.UtcNow.Add(CacheTtl));
        }
        return resolved;
    }

    private async Task<ResolvedTenant?> ResolveFromPostgresAsync(string tenantId, string apiKey, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Drive the lookup off the Kamsora-pattern select-fn and the tenants table.
        // We fetch every non-revoked key for the tenant, then bcrypt-compare in C#.
        // For typical tenants the result set is tiny (1-3 keys).
        await using var cmd = new NpgsqlCommand(@"
            SELECT t.systenantuuid, t.tenant_slug, t.status, k.sysapikeyuuid, k.key_hash, k.scopes, k.expires_at, k.revoked_at
              FROM public.mastertenants t
              JOIN public.masterapi_keys k ON k.systenantuuid = t.systenantuuid
             WHERE t.systenantuuid = @tenant_id
               AND t.status = 'active'
               AND k.revoked_at IS NULL
               AND (k.expires_at IS NULL OR k.expires_at > now())
        ", connection)
        {
            CommandTimeout = (int)_options.CommandTimeout.TotalSeconds,
        };
        cmd.Parameters.AddWithValue("tenant_id", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var keyHash = reader.GetString(reader.GetOrdinal("key_hash"));
            if (!BCryptVerify(apiKey, keyHash)) continue;

            var resolved = new ResolvedTenant(
                TenantId:   Guid.Parse(reader.GetString(reader.GetOrdinal("systenantuuid"))),
                TenantSlug: reader.GetString(reader.GetOrdinal("tenant_slug")),
                ApiKeyId:   reader.GetString(reader.GetOrdinal("sysapikeyuuid")),
                Scopes:     reader.IsDBNull(reader.GetOrdinal("scopes")) ? "ingest" : reader.GetString(reader.GetOrdinal("scopes")));

            return resolved;
        }

        return null;
    }

    /// <summary>
    /// Verify a cleartext API key against the stored bcrypt hash.
    /// Workaround: avoid taking a heavy bcrypt dependency in M1; we use the
    /// built-in <see cref="Microsoft.AspNetCore.Cryptography.KeyDerivation"/>
    /// PBKDF2 format (<c>$pbkdf2$…</c>) until M2 introduces BCrypt.Net.
    /// </summary>
    private static bool BCryptVerify(string cleartext, string storedHash)
    {
        // Stored hash format: $pbkdf2$<iterations>$<base64-salt>$<base64-hash>
        // For M1 we ship a single supported scheme; M2 adds BCrypt.Net-Next.
        const string scheme = "$pbkdf2$";
        if (!storedHash.StartsWith(scheme, StringComparison.Ordinal)) return false;

        var parts = storedHash[scheme.Length..].Split('$');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations) || iterations < 1) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt     = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var derived = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password: cleartext,
            salt:     salt,
            iterations: iterations,
            hashAlgorithm: System.Security.Cryptography.HashAlgorithmName.SHA256,
            outputLength: expected.Length);

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(derived, expected);
    }

    private static string CacheKey(string tenantId, string apiKey)
    {
        // Hash the api-key into the cache key so we don't keep cleartext in
        // process memory longer than necessary.
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(apiKey), hash);
        return string.Concat(tenantId, "|", Convert.ToHexString(hash));
    }

    private readonly record struct CacheEntry(ResolvedTenant Tenant, DateTime ExpiresAtUtc);
}
