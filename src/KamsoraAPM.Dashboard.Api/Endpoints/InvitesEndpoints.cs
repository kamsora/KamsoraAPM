// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// M4.2 user-invite flow. The owner mints an invite for a teammate's email,
/// receives a one-shot cleartext token (shown once, copied to clipboard, mailed
/// out-of-band), and the recipient submits the token + a chosen password at
/// <c>/accept-invite?token=…</c>.
///
/// SMTP-driven email delivery is intentionally deferred - we want zero
/// infra-dependencies for first deploy. When SMTP arrives we send the link
/// automatically; until then the owner copies it from the modal.
/// </summary>
public static class InvitesEndpoints
{
    private const int DefaultExpiryDays = 7;

    public static IEndpointRouteBuilder MapInvitesEndpoints(this IEndpointRouteBuilder app)
    {
        var tenant = app.MapGroup("/api/v1/tenant").RequireAuthorization(KamsoraPolicies.TenantOwner);

        // ---- Owner: list open invites for this tenant ----
        tenant.MapGet("/invites", async (
            HttpContext http,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();

            await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
            await using var cmd  = new NpgsqlCommand(@"
                SELECT sysinviteuuid, email, role, token_prefix,
                       expires_at, accepted_at, revoked_at, posteddatetime, postedby
                  FROM public.masterinvites
                 WHERE systenantuuid = @tenant
                 ORDER BY posteddatetime DESC
                 LIMIT 100", conn);
            cmd.Parameters.AddWithValue("tenant", tenantId.ToString());

            var list = new List<InviteSummary>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new InviteSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                    reader.IsDBNull(5) ? null : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                    reader.IsDBNull(6) ? null : DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                    DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
                    reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    DeriveStatus(
                        reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                        reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                        reader.GetDateTime(4))));
            }
            return Results.Ok(list);
        });

        // ---- Owner: mint a new invite ----
        tenant.MapPost("/invites", async (
            HttpContext http,
            CreateInviteRequest req,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Email))
                return Results.BadRequest(new { error = "email is required" });
            if (!req.Email.Contains('@', StringComparison.Ordinal))
                return Results.BadRequest(new { error = "email must contain '@'" });

            var role = string.IsNullOrWhiteSpace(req.Role) ? "viewer" : req.Role.Trim().ToLowerInvariant();
            if (role is not ("owner" or "admin" or "editor" or "viewer"))
                return Results.BadRequest(new { error = "role must be one of: owner, admin, editor, viewer" });

            // Refuse to invite an existing active user of this tenant - they're
            // already in. (Different tenants can share the same email.)
            await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
            await using var tx   = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using (var dup = new NpgsqlCommand(@"
                SELECT 1 FROM public.masterusers
                 WHERE systenantuuid = @tenant
                   AND lower(email)  = lower(@email)
                   AND status        = 'active'
                 LIMIT 1", conn, tx))
            {
                dup.Parameters.AddWithValue("tenant", tenantId.ToString());
                dup.Parameters.AddWithValue("email",  req.Email);
                if (await dup.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return Results.Conflict(new { error = "A user with that email already exists in this tenant." });
                }
            }

            var token       = GenerateInviteToken();
            var tokenHash   = Sha256Hex(token);
            var tokenPrefix = token[..8];
            var expiresAt   = DateTime.UtcNow.AddDays(DefaultExpiryDays);
            var actorUuid   = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

            string newInviteUuid;
            await using (var insert = new NpgsqlCommand(@"
                INSERT INTO public.masterinvites
                    (systenantuuid, email, role, token_hash, token_prefix,
                     invited_by_useruuid, expires_at, posteddatetime, postedby)
                VALUES (@tenant, @email, @role, @hash, @prefix,
                        @actor, @expires, CURRENT_TIMESTAMP, @by)
                RETURNING sysinviteuuid", conn, tx))
            {
                insert.Parameters.AddWithValue("tenant",  tenantId.ToString());
                insert.Parameters.AddWithValue("email",   req.Email);
                insert.Parameters.AddWithValue("role",    role);
                insert.Parameters.AddWithValue("hash",    tokenHash);
                insert.Parameters.AddWithValue("prefix",  tokenPrefix);
                insert.Parameters.AddWithValue("actor",   (object?)actorUuid ?? DBNull.Value);
                insert.Parameters.AddWithValue("expires", expiresAt);
                insert.Parameters.AddWithValue("by",      ActorTag(http));
                newInviteUuid = (string)(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            }

            await WriteAuditAsync(conn, tx, http, tenantId.ToString(), "invite.create",
                "masterinvites", newInviteUuid,
                JsonSerializer.Serialize(new { req.Email, role }), ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

            return Results.Ok(new CreateInviteResponse(
                newInviteUuid,
                req.Email,
                role,
                token,
                expiresAt));
        });

        // ---- Owner: revoke an invite ----
        tenant.MapDelete("/invites/{inviteUuid}", async (
            HttpContext http,
            string inviteUuid,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();

            await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
            await using var tx   = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            int rows;
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE public.masterinvites
                   SET revoked_at      = CURRENT_TIMESTAMP,
                       updatedby       = @by,
                       updateddatetime = CURRENT_TIMESTAMP
                 WHERE sysinviteuuid = @uuid
                   AND systenantuuid = @tenant
                   AND accepted_at  IS NULL
                   AND revoked_at   IS NULL", conn, tx))
            {
                cmd.Parameters.AddWithValue("by",     ActorTag(http));
                cmd.Parameters.AddWithValue("uuid",   inviteUuid);
                cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
                rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (rows == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return Results.NotFound(new { error = "Invite not found, already accepted, or already revoked." });
            }

            await WriteAuditAsync(conn, tx, http, tenantId.ToString(), "invite.revoke",
                "masterinvites", inviteUuid, null, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // ---- Anonymous: preview an invite (tenant slug + email + role) ----
        app.MapGet("/api/v1/invites/preview/{token}", async (
            string token,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            var resolved = await ResolveInviteAsync(pg, token, ct).ConfigureAwait(false);
            if (resolved is null) return Results.NotFound(new { error = "Invite is invalid, revoked, or expired." });

            return Results.Ok(new InvitePreview(
                resolved.Value.TenantSlug,
                resolved.Value.TenantName,
                resolved.Value.Email,
                resolved.Value.Role,
                resolved.Value.ExpiresAtUtc));
        });

        // ---- Anonymous: accept an invite, create user, return login JWT ----
        app.MapPost("/api/v1/invites/accept", async (
            HttpContext http,
            AcceptInviteRequest req,
            IOptions<PostgresOptions> pg,
            JwtIssuer issuer,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Token))
                return Results.BadRequest(new { error = "token is required" });
            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
                return Results.BadRequest(new { error = "password must be at least 8 characters" });

            var resolved = await ResolveInviteAsync(pg, req.Token, ct).ConfigureAwait(false);
            if (resolved is null) return Results.NotFound(new { error = "Invite is invalid, revoked, or expired." });

            await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
            await using var tx   = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Re-check inside the txn - protects against double-redeem races.
            await using (var lockCheck = new NpgsqlCommand(@"
                SELECT 1 FROM public.masterinvites
                 WHERE sysinviteuuid = @uuid
                   AND accepted_at IS NULL
                   AND revoked_at  IS NULL
                   AND expires_at  > CURRENT_TIMESTAMP
                 FOR UPDATE", conn, tx))
            {
                lockCheck.Parameters.AddWithValue("uuid", resolved.Value.InviteUuid);
                if (await lockCheck.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return Results.NotFound(new { error = "Invite is invalid, revoked, or expired." });
                }
            }

            // Idempotent user upsert: if the email already exists as 'invited' or
            // 'disabled', flip it to active with the new hash; otherwise insert.
            var displayName = string.IsNullOrWhiteSpace(req.DisplayName) ? resolved.Value.Email : req.DisplayName.Trim();
            var hash        = Pbkdf2.Hash(req.Password);

            string newUserUuid;
            await using (var upsert = new NpgsqlCommand(@"
                INSERT INTO public.masterusers
                    (systenantuuid, email, display_name, password_hash, role,
                     is_platform_admin, status, posteddatetime, postedby)
                VALUES (@tenant, @email, @display, @hash, @role,
                        false, 'active', CURRENT_TIMESTAMP, @by)
                ON CONFLICT (systenantuuid, email) DO UPDATE
                   SET display_name    = EXCLUDED.display_name,
                       password_hash   = EXCLUDED.password_hash,
                       role            = EXCLUDED.role,
                       status          = 'active',
                       updatedby       = @by,
                       updateddatetime = CURRENT_TIMESTAMP
                RETURNING sysuseruuid", conn, tx))
            {
                upsert.Parameters.AddWithValue("tenant",  resolved.Value.TenantId.ToString());
                upsert.Parameters.AddWithValue("email",   resolved.Value.Email);
                upsert.Parameters.AddWithValue("display", displayName);
                upsert.Parameters.AddWithValue("hash",    hash);
                upsert.Parameters.AddWithValue("role",    resolved.Value.Role);
                upsert.Parameters.AddWithValue("by",      $"invite:{resolved.Value.InviteUuid}");
                newUserUuid = (string)(await upsert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            }

            await using (var seal = new NpgsqlCommand(@"
                UPDATE public.masterinvites
                   SET accepted_at       = CURRENT_TIMESTAMP,
                       accepted_useruuid = @user,
                       updatedby         = @by,
                       updateddatetime   = CURRENT_TIMESTAMP
                 WHERE sysinviteuuid = @uuid", conn, tx))
            {
                seal.Parameters.AddWithValue("user", newUserUuid);
                seal.Parameters.AddWithValue("uuid", resolved.Value.InviteUuid);
                seal.Parameters.AddWithValue("by",   $"invite:{resolved.Value.InviteUuid}");
                await seal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await WriteAuditAsync(conn, tx, http, resolved.Value.TenantId.ToString(), "invite.accept",
                "masterusers", newUserUuid,
                JsonSerializer.Serialize(new { resolved.Value.Email, resolved.Value.Role }), ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

            var jwt = issuer.IssueForUser(
                Guid.Parse(newUserUuid),
                resolved.Value.Email,
                resolved.Value.TenantId,
                resolved.Value.TenantSlug,
                resolved.Value.Role,
                isPlatformAdmin: false);

            return Results.Ok(new AcceptInviteResponse(
                jwt,
                resolved.Value.TenantId.ToString(),
                resolved.Value.TenantSlug,
                resolved.Value.Role,
                IsPlatformAdmin: false));
        }).RequireRateLimiting("auth");

        return app;
    }

    // ---- Internals -------------------------------------------------------

    private readonly record struct ResolvedInvite(
        string   InviteUuid,
        Guid     TenantId,
        string   TenantSlug,
        string   TenantName,
        string   Email,
        string   Role,
        DateTime ExpiresAtUtc);

    private static async Task<ResolvedInvite?> ResolveInviteAsync(
        IOptions<PostgresOptions> pg, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 12) return null;
        var prefix = token[..8];
        var hash   = Sha256Hex(token);

        await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            SELECT i.sysinviteuuid, i.email, i.role, i.expires_at,
                   t.systenantuuid, t.tenant_slug, t.tenant_name
              FROM public.masterinvites i
              JOIN public.mastertenants t ON t.systenantuuid = i.systenantuuid
             WHERE i.token_prefix = @prefix
               AND i.token_hash   = @hash
               AND i.accepted_at IS NULL
               AND i.revoked_at  IS NULL
               AND i.expires_at  > CURRENT_TIMESTAMP
               AND t.status      = 'active'
             LIMIT 1", conn);
        cmd.Parameters.AddWithValue("prefix", prefix);
        cmd.Parameters.AddWithValue("hash",   hash);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        return new ResolvedInvite(
            InviteUuid:   reader.GetString(0),
            Email:        reader.GetString(1),
            Role:         reader.GetString(2),
            ExpiresAtUtc: DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
            TenantId:     Guid.Parse(reader.GetString(4)),
            TenantSlug:   reader.GetString(5),
            TenantName:   reader.GetString(6));
    }

    private static string DeriveStatus(DateTime? acceptedAt, DateTime? revokedAt, DateTime expiresAt)
    {
        if (acceptedAt is not null) return "accepted";
        if (revokedAt  is not null) return "revoked";
        if (expiresAt < DateTime.UtcNow) return "expired";
        return "pending";
    }

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

    private static string GenerateInviteToken()
    {
        // 32 random bytes → 64-char hex. Prefixed for visual recognisability
        // and to scope the lookup index. Same shape as ingest API keys.
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        return "kinv_" + Convert.ToHexString(raw).ToLowerInvariant();
    }

    private static string Sha256Hex(string input)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

// ---- DTOs ------------------------------------------------------------------

public sealed record InviteSummary(
    string    SysInviteUuid,
    string    Email,
    string    Role,
    string    TokenPrefix,
    DateTime  ExpiresAtUtc,
    DateTime? AcceptedAtUtc,
    DateTime? RevokedAtUtc,
    DateTime  CreatedAtUtc,
    string    CreatedBy,
    string    Status);   // pending | accepted | revoked | expired

public sealed record CreateInviteRequest(
    string  Email,
    string? Role = null);

public sealed record CreateInviteResponse(
    string   SysInviteUuid,
    string   Email,
    string   Role,
    string   Token,
    DateTime ExpiresAtUtc);

public sealed record InvitePreview(
    string   TenantSlug,
    string   TenantName,
    string   Email,
    string   Role,
    DateTime ExpiresAtUtc);

public sealed record AcceptInviteRequest(
    string  Token,
    string  Password,
    string? DisplayName = null);

public sealed record AcceptInviteResponse(
    string AccessToken,
    string TenantId,
    string TenantSlug,
    string Role,
    bool   IsPlatformAdmin);
