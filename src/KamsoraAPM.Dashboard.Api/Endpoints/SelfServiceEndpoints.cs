// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// M4.2 endpoints that any logged-in dashboard user can hit for themselves:
///   <list type="bullet">
///     <item><c>POST /api/v1/me/change-password</c> — verify old, set new.</item>
///     <item><c>GET  /api/v1/tenant/audit-log</c>   — paginated log for the caller's tenant (owner only).</item>
///     <item><c>GET  /api/v1/admin/audit-log</c>    — cross-tenant log (platform admin only).</item>
///   </list>
/// </summary>
public static class SelfServiceEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize     = 200;

    public static IEndpointRouteBuilder MapSelfServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var me     = app.MapGroup("/api/v1/me")    .RequireAuthorization();
        var owner  = app.MapGroup("/api/v1/tenant").RequireAuthorization(KamsoraPolicies.TenantOwner);
        var admin  = app.MapGroup("/api/v1/admin") .RequireAuthorization(KamsoraPolicies.PlatformAdmin);

        // ---- Change own password ----
        me.MapPost("/change-password", async (
            HttpContext http,
            ChangePasswordRequest req,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.OldPassword) || string.IsNullOrWhiteSpace(req.NewPassword))
                return Results.BadRequest(new { error = "oldPassword and newPassword are required" });
            if (req.NewPassword.Length < 8)
                return Results.BadRequest(new { error = "newPassword must be at least 8 characters" });
            if (req.OldPassword == req.NewPassword)
                return Results.BadRequest(new { error = "newPassword must differ from oldPassword" });

            var userUuid = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrEmpty(userUuid)) return Results.Unauthorized();

            await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);
            await using var tx   = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            string? storedHash;
            string  tenantUuid;
            await using (var fetch = new NpgsqlCommand(@"
                SELECT password_hash, systenantuuid
                  FROM public.masterusers
                 WHERE sysuseruuid = @uuid
                   AND status = 'active'
                 FOR UPDATE", conn, tx))
            {
                fetch.Parameters.AddWithValue("uuid", userUuid);
                await using var reader = await fetch.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return Results.Unauthorized();
                }
                storedHash = reader.IsDBNull(0) ? null : reader.GetString(0);
                tenantUuid = reader.GetString(1);
            }

            if (string.IsNullOrEmpty(storedHash) || !Pbkdf2.Verify(req.OldPassword, storedHash))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return Results.BadRequest(new { error = "Current password is incorrect." });
            }

            var newHash = Pbkdf2.Hash(req.NewPassword);
            await using (var update = new NpgsqlCommand(@"
                UPDATE public.masterusers
                   SET password_hash   = @hash,
                       updatedby       = @by,
                       updateddatetime = CURRENT_TIMESTAMP
                 WHERE sysuseruuid = @uuid", conn, tx))
            {
                update.Parameters.AddWithValue("hash", newHash);
                update.Parameters.AddWithValue("by",   ActorTag(http));
                update.Parameters.AddWithValue("uuid", userUuid);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await WriteAuditAsync(conn, tx, http, tenantUuid, "user.password.change",
                "masterusers", userUuid, null, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // ---- Audit log: tenant owner sees their own tenant ----
        owner.MapGet("/audit-log", async (
            HttpContext http,
            string? action,
            string? actor,
            int? page,
            int? pageSize,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tenantId)) return Results.Unauthorized();
            return await ListAuditAsync(pg, tenantId.ToString(), action, actor, page, pageSize, ct).ConfigureAwait(false);
        });

        // ---- Audit log: platform admin sees any/all tenants ----
        admin.MapGet("/audit-log", async (
            string? tenantUuid,
            string? action,
            string? actor,
            int? page,
            int? pageSize,
            IOptions<PostgresOptions> pg,
            CancellationToken ct) =>
        {
            if (tenantUuid is not null && !Guid.TryParse(tenantUuid, out _))
                return Results.BadRequest(new { error = "tenantUuid must be a UUID." });
            return await ListAuditAsync(pg, tenantUuid, action, actor, page, pageSize, ct).ConfigureAwait(false);
        });

        return app;
    }

    // ---- Internals -------------------------------------------------------

    private static async Task<IResult> ListAuditAsync(
        IOptions<PostgresOptions> pg,
        string? tenantUuid, string? action, string? actor,
        int? page, int? pageSize, CancellationToken ct)
    {
        var size   = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var offset = Math.Max(0, ((page ?? 1) - 1) * size);

        await using var conn = await OpenAsync(pg, ct).ConfigureAwait(false);

        // Build a parametrised WHERE — keeps the query plannable while letting
        // filters compose. `LIKE @action || '%'` matches "apikey.*" prefixes.
        var sb = new System.Text.StringBuilder();
        sb.Append(@"
            SELECT a.sysaudittransid, a.systenantuuid, a.actor_useruuid, u.email AS actor_email,
                   a.action, a.target_kind, a.target_uuid, a.client_ip::text, a.user_agent,
                   a.after_json::text, a.posteddatetime, a.postedby,
                   count(*) OVER ()
              FROM public.tblapm_audit_log a
              LEFT JOIN public.masterusers u
                ON u.sysuseruuid = a.actor_useruuid
             WHERE 1=1");
        if (!string.IsNullOrWhiteSpace(tenantUuid)) sb.Append(" AND a.systenantuuid = @tenant");
        if (!string.IsNullOrWhiteSpace(action))     sb.Append(" AND a.action LIKE @action || '%'");
        if (!string.IsNullOrWhiteSpace(actor))      sb.Append(" AND (u.email ILIKE '%' || @actor || '%' OR a.postedby ILIKE '%' || @actor || '%')");
        sb.Append(" ORDER BY a.posteddatetime DESC LIMIT @size OFFSET @off");

        await using var cmd = new NpgsqlCommand(sb.ToString(), conn);
        if (!string.IsNullOrWhiteSpace(tenantUuid)) cmd.Parameters.AddWithValue("tenant", tenantUuid);
        if (!string.IsNullOrWhiteSpace(action))     cmd.Parameters.AddWithValue("action", action.Trim());
        if (!string.IsNullOrWhiteSpace(actor))      cmd.Parameters.AddWithValue("actor",  actor.Trim());
        cmd.Parameters.AddWithValue("size", size);
        cmd.Parameters.AddWithValue("off",  offset);

        long total = 0;
        var list = new List<AuditLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AuditLogEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
            total = reader.GetInt64(12);
        }

        return Results.Ok(new AuditLogPage(list, total, page ?? 1, size));
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
}

// ---- DTOs ------------------------------------------------------------------

public sealed record ChangePasswordRequest(string OldPassword, string NewPassword);

public sealed record AuditLogEntry(
    string    SysAuditTransId,
    string    SysTenantUuid,
    string?   ActorUserUuid,
    string?   ActorEmail,
    string    Action,
    string?   TargetKind,
    string?   TargetUuid,
    string?   ClientIp,
    string?   UserAgent,
    string?   AfterJson,
    DateTime  PostedAtUtc,
    string?   PostedBy);

public sealed record AuditLogPage(
    IReadOnlyList<AuditLogEntry> Items,
    long  Total,
    int   Page,
    int   PageSize);
