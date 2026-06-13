// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using KamsoraAPM.Dashboard.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace KamsoraAPM.Dashboard.Api.Auth;

/// <summary>Issues short-lived signed JWTs for the dashboard SPA.</summary>
public sealed class JwtIssuer
{
    private readonly DashboardAuthOptions _options;

    public JwtIssuer(IOptions<DashboardAuthOptions> options) => _options = options.Value;

    public string IssueForUser(Guid userId, string email, Guid tenantId, string tenantSlug, string role, bool isPlatformAdmin)
    {
        var handler = new JwtSecurityTokenHandler();
        var key     = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSigningKey));
        var creds   = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub,   userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString("N")),
            new(KamsoraClaimTypes.TenantId,    tenantId.ToString()),
            new(KamsoraClaimTypes.TenantSlug,  tenantSlug),
            new(KamsoraClaimTypes.Role,        role),
        };
        if (isPlatformAdmin)
            claims.Add(new Claim(KamsoraClaimTypes.PlatformAdmin, "true"));

        var token = new JwtSecurityToken(
            issuer:             _options.JwtIssuer,
            audience:           _options.JwtAudience,
            claims:             claims,
            notBefore:          DateTime.UtcNow,
            expires:            DateTime.UtcNow.Add(_options.TokenLifetime),
            signingCredentials: creds);

        return handler.WriteToken(token);
    }
}
