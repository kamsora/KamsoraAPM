// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using KamsoraAPM.Dashboard.Api.Endpoints;
using KamsoraAPM.Dashboard.Api.Options;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KamsoraAPM.Dashboard.Api.Bootstrap;

/// <summary>
/// On startup, if no tenants exist in PostgreSQL and a <c>SeedTenant</c>
/// block is present in configuration, creates the tenant + admin user
/// + (optionally) one ingestion API key. The freshly-generated cleartext
/// API key is logged once at <c>Information</c> level - operators must
/// capture it from the logs.
///
/// Idempotent across restarts; if a tenant already exists the seeder is a no-op.
/// </summary>
internal sealed class TenantSeederHostedService : IHostedService
{
    private readonly DashboardAuthOptions _auth;
    private readonly PostgresOptions _pg;
    private readonly ILogger<TenantSeederHostedService> _logger;

    public TenantSeederHostedService(
        IOptions<DashboardAuthOptions> auth,
        IOptions<PostgresOptions> pg,
        ILogger<TenantSeederHostedService> logger)
    {
        _auth   = auth.Value;
        _pg     = pg.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_auth.SeedTenant is null)
        {
            return;
        }

        try
        {
            await SeedAsync(_auth.SeedTenant, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KamsoraAPM Dashboard.Api: tenant seeder failed; continuing startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedAsync(SeedTenantOptions seed, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // Existing tenants? Skip.
        await using (var check = new NpgsqlCommand("SELECT COUNT(*) FROM public.mastertenants WHERE status <> 'deleted'", conn))
        {
            var existing = Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (existing > 0)
            {
                _logger.LogInformation("KamsoraAPM Dashboard.Api: tenant seeding skipped - {Count} tenant(s) already present.", existing);
                return;
            }
        }

        // 1. Create tenant via fn_api_post_mastertenants.
        string tenantUuid;
        await using (var cmd = new NpgsqlCommand(
            "SELECT public.fn_api_post_mastertenants(@name, @slug, NULL, NULL, NULL, NULL, @by)", conn))
        {
            cmd.Parameters.AddWithValue("name", seed.TenantName);
            cmd.Parameters.AddWithValue("slug", seed.TenantSlug);
            cmd.Parameters.AddWithValue("by",   "system:seeder");
            tenantUuid = (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        // 2. Create admin user.
        var passwordHash = Pbkdf2.Hash(seed.AdminPassword);
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO public.masterusers(systenantuuid, email, display_name, password_hash, role, posteddatetime, postedby)
            VALUES (@tenant, @email, @display, @hash, 'owner', CURRENT_TIMESTAMP, 'system:seeder')", conn))
        {
            cmd.Parameters.AddWithValue("tenant",  tenantUuid);
            cmd.Parameters.AddWithValue("email",   seed.AdminEmail);
            cmd.Parameters.AddWithValue("display", seed.AdminEmail);
            cmd.Parameters.AddWithValue("hash",    passwordHash);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "KamsoraAPM Dashboard.Api: seeded tenant '{Slug}' ({Uuid}) with admin {Email}.",
            seed.TenantSlug, tenantUuid, seed.AdminEmail);

        // 3. Optional: issue a default ingestion API key.
        if (seed.IssueDefaultApiKey)
        {
            var cleartext = GenerateApiKey();
            var keyPrefix = cleartext[..8];
            var keyHash   = Pbkdf2.Hash(cleartext);

            await using var cmd = new NpgsqlCommand(
                "SELECT public.fn_api_post_masterapi_keys(@tenant, @name, @prefix, @hash, 'ingest', NULL, @by)", conn);
            cmd.Parameters.AddWithValue("tenant", tenantUuid);
            cmd.Parameters.AddWithValue("name",   "default-ingest");
            cmd.Parameters.AddWithValue("prefix", keyPrefix);
            cmd.Parameters.AddWithValue("hash",   keyHash);
            cmd.Parameters.AddWithValue("by",     "system:seeder");
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(@"
================================================================
 KamsoraAPM seeded an ingestion API key. SAVE IT NOW - this is
 the only time it will be displayed in plaintext.

   tenant_uuid : {Tenant}
   api_key     : {ApiKey}

 Configure your Agent with these exact values.
================================================================",
                tenantUuid, cleartext);
        }
    }

    private static string GenerateApiKey()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        // 32 bytes -> 64 hex chars (url-safe, no padding); prefix with "kapm_" for recognisability.
        return "kapm_" + Convert.ToHexString(raw).ToLowerInvariant();
    }
}
