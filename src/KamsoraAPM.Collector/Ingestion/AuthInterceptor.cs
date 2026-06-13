// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using Grpc.Core;
using Grpc.Core.Interceptors;
using KamsoraAPM.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// gRPC interceptor that authenticates ingestion calls by checking the
/// <c>x-kamsora-tenant</c> + <c>x-kamsora-api-key</c> metadata headers
/// against PostgreSQL via <see cref="ITenantResolver"/>.
///
/// On success the <see cref="ResolvedTenant"/> is attached to the call's
/// <see cref="ServerCallContext.UserState"/>; handlers retrieve it via
/// <see cref="TenantContextAccessor.GetTenant"/>. On failure the call is
/// terminated with <see cref="StatusCode.Unauthenticated"/>.
/// </summary>
public sealed class AuthInterceptor : Interceptor
{
    private const string TenantHeader = "x-kamsora-tenant";
    private const string ApiKeyHeader = "x-kamsora-api-key";

    private readonly ITenantResolver _resolver;
    private readonly ILogger<AuthInterceptor> _logger;

    public AuthInterceptor(ITenantResolver resolver, ILogger<AuthInterceptor> logger)
    {
        _resolver = resolver;
        _logger   = logger;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var tenant = await AuthenticateAsync(context).ConfigureAwait(false);
        TenantContextAccessor.SetTenant(context, tenant);
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var tenant = await AuthenticateAsync(context).ConfigureAwait(false);
        TenantContextAccessor.SetTenant(context, tenant);
        return await continuation(requestStream, context).ConfigureAwait(false);
    }

    private async Task<ResolvedTenant> AuthenticateAsync(ServerCallContext context)
    {
        string? tenantId = null;
        string? apiKey   = null;

        foreach (var entry in context.RequestHeaders)
        {
            if (entry.IsBinary) continue;
            if (string.Equals(entry.Key, TenantHeader, StringComparison.OrdinalIgnoreCase))
                tenantId = entry.Value;
            else if (string.Equals(entry.Key, ApiKeyHeader, StringComparison.OrdinalIgnoreCase))
                apiKey = entry.Value;
        }

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("KamsoraAPM Collector: ingestion call missing required auth headers (method={Method}, peer={Peer}).",
                context.Method, context.Peer);
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                $"Missing required headers: {TenantHeader} and {ApiKeyHeader}."));
        }

        var resolved = await _resolver.ResolveAsync(tenantId, apiKey, context.CancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            _logger.LogWarning("KamsoraAPM Collector: rejected unauthenticated ingestion call (tenant={Tenant}, peer={Peer}).",
                tenantId, context.Peer);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid tenant or API key."));
        }

        return resolved;
    }
}
