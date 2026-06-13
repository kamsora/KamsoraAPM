// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// Minimal username/password login for M1. Validates the credential against
/// <c>masterusers.password_hash</c> using PBKDF2 and issues a JWT carrying the
/// tenant claim. SSO / OAuth / Keycloak integration arrives in M5.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/login", async (
            LoginRequest req,
            IOptions<PostgresOptions> pgOptions,
            JwtIssuer issuer,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "email and password are required" });

            await using var conn = new NpgsqlConnection(pgOptions.Value.ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand(@"
                SELECT u.sysuseruuid, u.email, u.password_hash, u.role, u.is_platform_admin,
                       t.systenantuuid, t.tenant_slug, t.status
                  FROM public.masterusers u
                  JOIN public.mastertenants t ON t.systenantuuid = u.systenantuuid
                 WHERE u.email = @email
                   AND u.status = 'active'
                   AND t.status = 'active'
                 LIMIT 1
            ", conn);
            cmd.Parameters.AddWithValue("email", req.Email);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Results.Unauthorized();

            var storedHash = reader.GetString(reader.GetOrdinal("password_hash"));
            if (!Pbkdf2.Verify(req.Password, storedHash))
                return Results.Unauthorized();

            var userId          = Guid.Parse(reader.GetString(reader.GetOrdinal("sysuseruuid")));
            var email           = reader.GetString(reader.GetOrdinal("email"));
            var role            = reader.GetString(reader.GetOrdinal("role"));
            var isPlatformAdmin = reader.GetBoolean(reader.GetOrdinal("is_platform_admin"));
            var tenantId        = Guid.Parse(reader.GetString(reader.GetOrdinal("systenantuuid")));
            var tenantSlug      = reader.GetString(reader.GetOrdinal("tenant_slug"));

            var jwt = issuer.IssueForUser(userId, email, tenantId, tenantSlug, role, isPlatformAdmin);
            return Results.Ok(new LoginResponse(jwt, tenantId.ToString(), tenantSlug, role, isPlatformAdmin));
        }).RequireRateLimiting("auth");

        return app;
    }
}

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string AccessToken, string TenantId, string TenantSlug, string Role, bool IsPlatformAdmin);

/// <summary>Shared PBKDF2 hash + verify used by the auth path and the tenant seeder.</summary>
public static class Pbkdf2
{
    private const int Iterations  = 100_000;
    private const int SaltBytes   = 16;
    private const int HashBytes   = 32;
    private const string Scheme   = "$pbkdf2$";

    public static string Hash(string cleartext)
    {
        var salt    = RandomNumberGenerator.GetBytes(SaltBytes);
        var derived = Rfc2898DeriveBytes.Pbkdf2(cleartext, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(derived)}";
    }

    public static bool Verify(string cleartext, string storedHash)
    {
        if (!storedHash.StartsWith(Scheme, StringComparison.Ordinal)) return false;
        var parts = storedHash[Scheme.Length..].Split('$');
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

        var derived = Rfc2898DeriveBytes.Pbkdf2(cleartext, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(derived, expected);
    }
}
