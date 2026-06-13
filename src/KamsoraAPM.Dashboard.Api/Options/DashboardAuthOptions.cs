// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel.DataAnnotations;

namespace KamsoraAPM.Dashboard.Api.Options;

/// <summary>Dashboard.Api JWT-bearer configuration.</summary>
public sealed class DashboardAuthOptions
{
    /// <summary>
    /// HMAC-SHA256 signing key (base64). Must be 32 bytes after decoding.
    /// Provided via <c>KamsoraApm:Auth:JwtSigningKey</c> in config.
    /// </summary>
    [Required, MinLength(32)]
    public string JwtSigningKey { get; set; } = string.Empty;

    /// <summary>JWT issuer claim. Defaults to <c>kamsora-apm</c>.</summary>
    public string JwtIssuer { get; set; } = "kamsora-apm";

    /// <summary>JWT audience claim. Defaults to <c>kamsora-apm-dashboard</c>.</summary>
    public string JwtAudience { get; set; } = "kamsora-apm-dashboard";

    /// <summary>Token lifetime. Defaults to 8 hours.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Optional bootstrap tenant. When set and the database has no tenants yet,
    /// the Dashboard.Api seeds this tenant on startup so the self-hosted
    /// single-org path works out-of-the-box.
    /// </summary>
    public SeedTenantOptions? SeedTenant { get; set; }
}

/// <summary>Bootstrap tenant supplied via configuration for first-run experience.</summary>
public sealed class SeedTenantOptions
{
    [Required] public string TenantName { get; set; } = string.Empty;
    [Required] public string TenantSlug { get; set; } = string.Empty;
    [Required] public string AdminEmail { get; set; } = string.Empty;
    [Required] public string AdminPassword { get; set; } = string.Empty;
    /// <summary>If true, the seeder also creates an ingestion API key and logs it once.</summary>
    public bool IssueDefaultApiKey { get; set; } = true;
}
