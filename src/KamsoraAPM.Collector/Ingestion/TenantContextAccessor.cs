// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using Grpc.Core;
using KamsoraAPM.Storage.Abstractions;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// Stashes / retrieves the <see cref="ResolvedTenant"/> on the gRPC
/// <see cref="ServerCallContext"/>'s <c>UserState</c> dictionary.
/// </summary>
internal static class TenantContextAccessor
{
    private const string UserStateKey = "kamsora.tenant";

    public static void SetTenant(ServerCallContext context, ResolvedTenant tenant)
        => context.UserState[UserStateKey] = tenant;

    public static ResolvedTenant GetTenant(ServerCallContext context)
        => context.UserState.TryGetValue(UserStateKey, out var value) && value is ResolvedTenant t
            ? t
            : throw new RpcException(new Status(StatusCode.Unauthenticated,
                "KamsoraAPM Collector: no tenant context attached to this call. Auth interceptor not configured?"));
}
