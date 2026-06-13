// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Compression;
using KamsoraAPM.Agent.Options;

namespace KamsoraAPM.Agent.Internal;

/// <summary>
/// Shared factory for the gRPC channel + per-call metadata used by every
/// signal-specific exporter (traces, logs, metrics, profiles). Keeps channel
/// tuning, keepalive, and on-the-wire gzip compression consistent across
/// signals - set once, applied everywhere.
/// </summary>
internal static class KamsoraGrpcChannelFactory
{
    /// <summary>
    /// Build a long-lived <see cref="GrpcChannel"/> targeting the configured
    /// Collector endpoint. Enables multiple HTTP/2 connections, keepalive
    /// pings, and registers the gzip codec so the Collector can decompress
    /// outbound batches.
    /// </summary>
    public static GrpcChannel Create(KamsoraApmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return GrpcChannel.ForAddress(options.Endpoint, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime       = TimeSpan.FromMinutes(5),
                KeepAlivePingDelay             = TimeSpan.FromSeconds(20),
                KeepAlivePingTimeout           = TimeSpan.FromSeconds(10),
            },
            CompressionProviders = new List<ICompressionProvider>
            {
                // Fastest level keeps the CPU bill for the Agent low; pprof
                // and log batches still get ~5-10x reduction because they
                // are text/protobuf-heavy.
                new GzipCompressionProvider(CompressionLevel.Fastest),
            },
        });
    }

    /// <summary>
    /// Build a per-call <see cref="Metadata"/> bundle with auth headers AND
    /// the gzip request-encoding hint that tells grpc-dotnet to compress the
    /// outgoing payload. <see cref="GrpcChannelOptions.CompressionProviders"/>
    /// only REGISTERS the codec; the per-call header is what actually engages it.
    /// </summary>
    public static Metadata BuildAuthMetadata(KamsoraApmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Metadata
        {
            { "x-kamsora-tenant",                 options.TenantId },
            { "x-kamsora-api-key",                options.ApiKey   },
            { "grpc-internal-encoding-request",   "gzip"           },
        };
    }
}
