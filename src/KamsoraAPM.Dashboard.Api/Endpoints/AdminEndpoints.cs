// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text.Json;
using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// M4.1 platform-admin and tenant-owner endpoints. Two route groups:
///   <list type="bullet">
///     <item><c>/api/v1/admin/*</c> — platform admin only, can act on any tenant.</item>
///     <item><c>/api/v1/tenant/*</c> — tenant owner, scoped to their own tenant via JWT.</item>
///   </list>
/// Every mutating call writes a row to <c>tblapm_audit_log</c>.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin  = app.MapGroup("/api/v1/admin") .RequireAuthorization(KamsoraPolicies.PlatformAdmin);
        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization(KamsoraPolicies.TenantOwner);

        // ---- Platform-admin: tenants ----

        admin.MapGet("/tenants", async (
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(@"
                SELECT t.systenantuuid, t.tenant_name, t.tenant_slug, t.plan_type,
                       t.data_retention_days, t.status, t.contact_email,
                       t.posteddatetime,
                       (SELECT count(*) FROM public.masterusers u
                          WHERE u.systenantuuid = t.systenantuuid AND u.status = 'active') AS user_count,
                       (SELECT count(*) FROM public.masterapi_keys k
                          WHERE k.systenantuuid = t.systenantuuid AND k.revoked_at IS NULL) AS api_key_count
                  FROM public.mastertenants t
                 WHERE t.status <> 'deleted'
                 ORDER BY t.posteddatetime DESC", conn);

            var list = new List<TenantSummary>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new TenantSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
                    reader.GetInt64(8),
                    reader.GetInt64(9)));
            }
            return Results.Ok(list);
        });

        admin.MapPost("/tenants", async (
            HttpContext http,
            CreateTenantRequest req,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            var validation = ValidateCreateTenant(req);
            if (validation is not null) return Results.BadRequest(new { error = validation });

            await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
            await using var tx   = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            try
            {
                // 1. Create tenant
                await using var createTenant = new NpgsqlCommand(
                    "SELECT public.fn_api_post_mastertenants(@name, @slug, @plan, @retention, NULL, @email, @by)", conn, tx);
                createTenant.Parameters.AddWithValue("name",      req.TenantName);
                createTenant.Parameters.AddWithValue("slug",      req.TenantSlug);
                createTenant.Parameters.AddWithValue("plan",      (object?)req.PlanType ?? DBNull.Value);
                createTenant.Parameters.AddWithValue("retention", (object?)req.RetentionDays ?? DBNull.Value);
                createTenant.Parameters.AddWithValue("email",     (object?)req.ContactEmail ?? DBNull.Value);
                createTenant.Parameters.AddWithValue("by",        ActorTag(http));
                var newTenantUuid = (string)(await createTenant.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

                // 2. Create owner user (with random temp password)
                var tempPassword = GenerateTempPassword();
                var hash         = Pbkdf2.Hash(tempPassword);
                await using (var createUser = new NpgsqlCommand(@"
                    INSERT INTO public.masterusers
                        (systenantuuid, email, display_name, password_hash, role,
                         is_platform_admin, posteddatetime, postedby)
                    VALUES (@tenant, @email, @display, @hash, 'owner',
                            false, CURRENT_TIMESTAMP, @by)", conn, tx))
                {
                    createUser.Parameters.AddWithValue("tenant",  newTenantUuid);
                    createUser.Parameters.AddWithValue("email",   req.OwnerEmail);
                    createUser.Parameters.AddWithValue("display", req.OwnerEmail);
                    createUser.Parameters.AddWithValue("hash",    hash);
                    createUser.Parameters.AddWithValue("by",      ActorTag(http));
                    await createUser.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // 3. Mint an initial ingest API key
                var apiKey       = GenerateApiKey();
                var keyPrefix    = apiKey[..8];
                var keyHash      = Pbkdf2.Hash(apiKey);
                await using (var createKey = new NpgsqlCommand(
                    "SELECT public.fn_api_post_masterapi_keys(@tenant, @name, @prefix, @hash, 'ingest', NULL, @by)", conn, tx))
                {
                    createKey.Parameters.AddWithValue("tenant", newTenantUuid);
                    createKey.Parameters.AddWithValue("name",   "default-ingest");
                    createKey.Parameters.AddWithValue("prefix", keyPrefix);
                    createKey.Parameters.AddWithValue("hash",   keyHash);
                    createKey.Parameters.AddWithValue("by",     ActorTag(http));
                    await createKey.ExecuteScalarAsync(ct).ConfigureAwait(false);
                }

                await WriteAuditAsync(conn, tx, http, newTenantUuid, "tenant.create",
                    "mastertenants", newTenantUuid, JsonSerializer.Serialize(new
                    {
                        req.TenantName, req.TenantSlug, req.PlanType, req.OwnerEmail
                    }), ct).ConfigureAwait(false);

                await tx.CommitAsync(ct).ConfigureAwait(false);

                return Results.Ok(new CreateTenantResponse(
                    newTenantUuid,
                    req.TenantSlug,
                    req.OwnerEmail,
                    tempPassword,
                    apiKey));
            }
            catch (PostgresException pex) when (pex.SqlState == "23505")
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return Results.Conflict(new { error = "Tenant slug or owner email already exists." });
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        });

        // ---- Platform-admin: mint key for ANY tenant ----

        admin.MapPost("/tenants/{tenantUuid}/api-keys", async (
            HttpContext http,
            string tenantUuid,
            MintApiKeyRequest req,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(tenantUuid, out _))
                return Results.BadRequest(new { error = "tenantUuid must be a UUID." });
            return await MintKeyInternalAsync(http, pg, tenantUuid, req, ct).ConfigureAwait(false);
        });

        admin.MapGet("/tenants/{tenantUuid}/api-keys", async (
            HttpContext http,
            string tenantUuid,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(tenantUuid, out _))
                return Results.BadRequest(new { error = "tenantUuid must be a UUID." });
            return await ListKeysInternalAsync(pg, tenantUuid, ct).ConfigureAwait(false);
        });

        admin.MapDelete("/tenants/{tenantUuid}/api-keys/{keyUuid}", async (
            HttpContext http,
            string tenantUuid,
            string keyUuid,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            return await RevokeKeyInternalAsync(http, pg, tenantUuid, keyUuid, ct).ConfigureAwait(false);
        });

        // ---- Platform-admin: suspend / resume / soft-delete a tenant ----
        // suspend = status='suspended' — blocks dashboard login (AuthEndpoints
        // filters on status='active') and ingest (PostgresTenantResolver does
        // the same, with up to 5 min lag due to the in-memory cache).
        // delete  = status='deleted'  — soft; ClickHouse data retained.
        admin.MapPost("/tenants/{tenantUuid}/suspend", async (
            HttpContext http, string tenantUuid,
            IOptions<PostgresOptions> pg, CancellationToken ct) =>
                await ChangeTenantStatusAsync(http, pg, tenantUuid, "suspended", "tenant.suspend", ct).ConfigureAwait(false));

        admin.MapPost("/tenants/{tenantUuid}/resume", async (
            HttpContext http, string tenantUuid,
            IOptions<PostgresOptions> pg, CancellationToken ct) =>
                await ChangeTenantStatusAsync(http, pg, tenantUuid, "active", "tenant.resume", ct).ConfigureAwait(false));

        admin.MapDelete("/tenants/{tenantUuid}", async (
            HttpContext http, string tenantUuid,
            IOptions<PostgresOptions> pg, CancellationToken ct) =>
                await ChangeTenantStatusAsync(http, pg, tenantUuid, "deleted", "tenant.delete", ct).ConfigureAwait(false));

        // ---- Tenant-owner: manage current tenant's API keys ----

        tenant.MapGet("/api-keys", async (
            HttpContext http,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            return await ListKeysInternalAsync(pg, tenantId.ToString(), ct).ConfigureAwait(false);
        });

        tenant.MapPost("/api-keys", async (
            HttpContext http,
            MintApiKeyRequest req,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            return await MintKeyInternalAsync(http, pg, tenantId.ToString(), req, ct).ConfigureAwait(false);
        });

        tenant.MapDelete("/api-keys/{keyUuid}", async (
            HttpContext http,
            string keyUuid,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            return await RevokeKeyInternalAsync(http, pg, tenantId.ToString(), keyUuid, ct).ConfigureAwait(false);
        });

        return app;
    }

    // ---- Internals -------------------------------------------------------

    private static async Task<IResult> ListKeysInternalAsync(
        IOptions<PostgresOptions> pg, string tenantUuid, CancellationToken ct)
    {
        await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            SELECT sysapikeyuuid, key_name, key_prefix, scopes,
                   expires_at, last_used_at, posteddatetime, postedby
              FROM public.masterapi_keys
             WHERE systenantuuid = @tenant
               AND revoked_at IS NULL
             ORDER BY posteddatetime DESC", conn);
        cmd.Parameters.AddWithValue("tenant", tenantUuid);

        var keys = new List<ApiKeySummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(new ApiKeySummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? "ingest" : reader.GetString(3),
                reader.IsDBNull(4) ? null : (DateTime?)DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                reader.IsDBNull(5) ? null : (DateTime?)DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7)));
        }
        return Results.Ok(keys);
    }

    private static async Task<IResult> MintKeyInternalAsync(
        HttpContext http, IOptions<PostgresOptions> pg, string tenantUuid, MintApiKeyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.KeyName))
            return Results.BadRequest(new { error = "keyName is required" });

        var apiKey    = GenerateApiKey();
        var keyPrefix = apiKey[..8];
        var keyHash   = Pbkdf2.Hash(apiKey);
        var scopes    = string.IsNullOrWhiteSpace(req.Scopes) ? "ingest" : req.Scopes;

        await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
        await using var tx   = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        string newKeyUuid;
        await using (var cmd = new NpgsqlCommand(
            "SELECT public.fn_api_post_masterapi_keys(@tenant, @name, @prefix, @hash, @scopes, @exp, @by)", conn, tx))
        {
            cmd.Parameters.AddWithValue("tenant", tenantUuid);
            cmd.Parameters.AddWithValue("name",   req.KeyName);
            cmd.Parameters.AddWithValue("prefix", keyPrefix);
            cmd.Parameters.AddWithValue("hash",   keyHash);
            cmd.Parameters.AddWithValue("scopes", scopes);
            cmd.Parameters.AddWithValue("exp",    (object?)req.ExpiresAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("by",     ActorTag(http));
            newKeyUuid = (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        await WriteAuditAsync(conn, tx, http, tenantUuid, "apikey.create",
            "masterapi_keys", newKeyUuid,
            JsonSerializer.Serialize(new { req.KeyName, scopes, keyPrefix }), ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

        return Results.Ok(new MintApiKeyResponse(newKeyUuid, keyPrefix, apiKey));
    }

    private static async Task<IResult> ChangeTenantStatusAsync(
        HttpContext http, IOptions<PostgresOptions> pg,
        string tenantUuid, string newStatus, string auditAction, CancellationToken ct)
    {
        if (!Guid.TryParse(tenantUuid, out _))
            return Results.BadRequest(new { error = "tenantUuid must be a UUID." });

        await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
        await using var tx   = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        int rows;
        await using (var cmd = new NpgsqlCommand(
            "SELECT public.fn_api_post_tenant_status(@uuid, @status, @by)", conn, tx))
        {
            cmd.Parameters.AddWithValue("uuid",   tenantUuid);
            cmd.Parameters.AddWithValue("status", newStatus);
            cmd.Parameters.AddWithValue("by",     ActorTag(http));
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        // The fn is no-op if the tenant is already 'deleted' — surface that as 404.
        await using (var check = new NpgsqlCommand(
            "SELECT status FROM public.mastertenants WHERE systenantuuid = @uuid", conn, tx))
        {
            check.Parameters.AddWithValue("uuid", tenantUuid);
            var current = await check.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (current is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return Results.NotFound(new { error = "Tenant not found." });
            }
            if (!string.Equals(current, newStatus, StringComparison.Ordinal))
            {
                // Soft-delete is terminal; fn refused to flip.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return Results.Conflict(new { error = $"Tenant is '{current}' and cannot transition to '{newStatus}'." });
            }
            rows = 1;
        }

        if (rows > 0)
        {
            await WriteAuditAsync(conn, tx, http, tenantUuid, auditAction,
                "mastertenants", tenantUuid, null, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeKeyInternalAsync(
        HttpContext http, IOptions<PostgresOptions> pg, string tenantUuid, string keyUuid, CancellationToken ct)
    {
        await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
        await using var tx   = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        int rows;
        await using (var cmd = new NpgsqlCommand(@"
            UPDATE public.masterapi_keys
               SET revoked_at      = CURRENT_TIMESTAMP,
                   updatedby       = @by,
                   updateddatetime = CURRENT_TIMESTAMP
             WHERE sysapikeyuuid = @key
               AND systenantuuid = @tenant
               AND revoked_at IS NULL", conn, tx))
        {
            cmd.Parameters.AddWithValue("by",     ActorTag(http));
            cmd.Parameters.AddWithValue("key",    keyUuid);
            cmd.Parameters.AddWithValue("tenant", tenantUuid);
            rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (rows == 0)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return Results.NotFound(new { error = "API key not found, already revoked, or belongs to a different tenant." });
        }

        await WriteAuditAsync(conn, tx, http, tenantUuid, "apikey.revoke",
            "masterapi_keys", keyUuid, null, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    // ---- Helpers ---------------------------------------------------------

    private static async Task<NpgsqlConnection> OpenAsync(IOptions<PostgresOptions> pg, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(pg.Value.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
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

    private static string ActorTag(HttpContext http)
    {
        var email = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value;
        return string.IsNullOrEmpty(email) ? "system:dashboard" : $"user:{email}";
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, HttpContext http,
        string tenantUuid, string action, string targetKind, string targetUuid, string? afterJson,
        CancellationToken ct)
    {
        var actorUuid = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        var ip        = http.Connection.RemoteIpAddress?.ToString();
        var ua        = http.Request.Headers.UserAgent.ToString();

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.tblapm_audit_log
                (systenantuuid, actor_useruuid, action, target_kind, target_uuid,
                 after_json, client_ip, user_agent, posteddatetime, postedby)
            VALUES (@tenant, @actor, @action, @kind, @target,
                    @after::jsonb, @ip::inet, @ua, CURRENT_TIMESTAMP, @by)", conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantUuid);
        cmd.Parameters.AddWithValue("actor",  (object?)actorUuid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("kind",   targetKind);
        cmd.Parameters.AddWithValue("target", targetUuid);
        cmd.Parameters.AddWithValue("after",  (object?)afterJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ip",     (object?)ip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ua",     string.IsNullOrEmpty(ua) ? (object)DBNull.Value : ua);
        cmd.Parameters.AddWithValue("by",     ActorTag(http));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string? ValidateCreateTenant(CreateTenantRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TenantName)) return "tenantName is required";
        if (string.IsNullOrWhiteSpace(req.TenantSlug)) return "tenantSlug is required";
        if (string.IsNullOrWhiteSpace(req.OwnerEmail)) return "ownerEmail is required";
        if (!req.TenantSlug.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
            return "tenantSlug must be alphanumeric (dashes and underscores allowed)";
        if (req.PlanType is not null && req.PlanType is not ("free" or "pro" or "enterprise"))
            return "planType must be one of: free, pro, enterprise";
        return null;
    }

    private static string GenerateApiKey()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        return "kapm_" + Convert.ToHexString(raw).ToLowerInvariant();
    }

    private static string GenerateTempPassword()
    {
        // 18-char URL-safe base64 — enough entropy to resist brute force even
        // if it lives in screenshots / chat backlogs after first login.
        Span<byte> raw = stackalloc byte[12];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

// ---- DTOs ------------------------------------------------------------------

public sealed record TenantSummary(
    string   SysTenantUuid,
    string   TenantName,
    string   TenantSlug,
    string   PlanType,
    int      DataRetentionDays,
    string   Status,
    string   ContactEmail,
    DateTime CreatedAtUtc,
    long     UserCount,
    long     ApiKeyCount);

public sealed record CreateTenantRequest(
    string  TenantName,
    string  TenantSlug,
    string  OwnerEmail,
    string? PlanType       = null,
    int?    RetentionDays  = null,
    string? ContactEmail   = null);

public sealed record CreateTenantResponse(
    string TenantUuid,
    string TenantSlug,
    string OwnerEmail,
    string OwnerTempPassword,
    string IngestApiKey);

public sealed record ApiKeySummary(
    string    SysApiKeyUuid,
    string    KeyName,
    string    KeyPrefix,
    string    Scopes,
    DateTime? ExpiresAtUtc,
    DateTime? LastUsedAtUtc,
    DateTime  CreatedAtUtc,
    string    CreatedBy);

public sealed record MintApiKeyRequest(
    string  KeyName,
    string? Scopes    = null,
    DateTime? ExpiresAt = null);

public sealed record MintApiKeyResponse(
    string SysApiKeyUuid,
    string KeyPrefix,
    string Cleartext);
