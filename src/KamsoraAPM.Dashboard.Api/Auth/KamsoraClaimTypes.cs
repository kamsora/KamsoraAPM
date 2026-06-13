// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Dashboard.Api.Auth;

/// <summary>Custom JWT claim names KamsoraAPM emits.</summary>
public static class KamsoraClaimTypes
{
    /// <summary>UUID of the tenant the bearer belongs to.</summary>
    public const string TenantId   = "kamsora_tenant";
    /// <summary>Slug of the tenant for display.</summary>
    public const string TenantSlug = "kamsora_tenant_slug";
    /// <summary>Role: owner / admin / editor / viewer.</summary>
    public const string Role       = "kamsora_role";
    /// <summary>"true" when the bearer can administer ALL tenants (M4.1 platform-admin role).</summary>
    public const string PlatformAdmin = "kamsora_platform_admin";
}

/// <summary>Names of <see cref="Microsoft.AspNetCore.Authorization"/> policies registered in Program.cs.</summary>
public static class KamsoraPolicies
{
    /// <summary>Allows only callers whose JWT carries <c>kamsora_platform_admin=true</c>.</summary>
    public const string PlatformAdmin = "PlatformAdmin";
    /// <summary>Allows callers whose tenant role is <c>owner</c> (full control of their tenant).</summary>
    public const string TenantOwner   = "TenantOwner";
}
