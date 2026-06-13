// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using KamsoraAPM.Agent.Internal;
using KamsoraAPM.Agent.Options;
using KamsoraAPM.Contracts.Collector.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProtoProfile      = KamsoraAPM.Contracts.Profiles.V1.Profile;
using ProtoResProfiles  = KamsoraAPM.Contracts.Profiles.V1.ResourceProfiles;
using ProtoScopeProfiles = KamsoraAPM.Contracts.Profiles.V1.ScopeProfiles;
using ProtoKind         = KamsoraAPM.Contracts.Profiles.V1.ProfileKind;
using ProtoInstScope    = KamsoraAPM.Contracts.Common.V1.InstrumentationScope;
using ProtoResource     = KamsoraAPM.Contracts.Common.V1.Resource;
using ProtoKv           = KamsoraAPM.Contracts.Common.V1.KeyValue;
using ProtoAnyValue     = KamsoraAPM.Contracts.Common.V1.AnyValue;

namespace KamsoraAPM.Agent.Internal.Profiling;

/// <summary>
/// Ships a single CPU profile capture (already converted to pprof bytes) to
/// the KamsoraAPM Collector over gRPC. One <see cref="ProfileCapture"/> in,
/// one <c>ProfilesService.Export</c> gRPC call out.
/// </summary>
internal sealed class KamsoraApmProfileExporter : IDisposable
{
    private readonly KamsoraApmOptions _options;
    private readonly ILogger<KamsoraApmProfileExporter> _logger;
    private readonly GrpcChannel _grpcChannel;
    private readonly ProfilesService.ProfilesServiceClient _client;
    private readonly ProtoResource _resource;

    public KamsoraApmProfileExporter(IOptions<KamsoraApmOptions> options, ILogger<KamsoraApmProfileExporter> logger)
    {
        _options     = options.Value;
        _logger      = logger;
        _grpcChannel = KamsoraGrpcChannelFactory.Create(_options);
        _client      = new ProfilesService.ProfilesServiceClient(_grpcChannel);
        _resource    = BuildResource(_options);
    }

    public async Task ExportAsync(ProfileCapture capture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var proto = new ProtoProfile
        {
            StartTimeUnixNano = ToUnixNanos(capture.StartUtc),
            DurationNano      = (ulong)Math.Max(0, capture.Duration.Ticks * 100L),
            Kind              = capture.Kind,
            SampleCount       = (ulong)Math.Max(0, capture.SampleCount),
            Pprof             = ByteString.CopyFrom(capture.PprofBytes),
        };
        if (capture.TriggerTraceId is { Length: 16 })
            proto.TriggerTraceId = ByteString.CopyFrom(capture.TriggerTraceId);

        var scope = new ProtoScopeProfiles
        {
            Scope = new ProtoInstScope { Name = "KamsoraAPM.Agent", Version = KamsoraApmAgent.Version },
        };
        scope.Profiles.Add(proto);

        var resourceProfiles = new ProtoResProfiles { Resource = _resource };
        resourceProfiles.ScopeProfiles.Add(scope);

        var request = new ExportProfilesRequest();
        request.ResourceProfiles.Add(resourceProfiles);

        var headers = KamsoraGrpcChannelFactory.BuildAuthMetadata(_options);

        try
        {
            using var call = _client.ExportAsync(
                request,
                new CallOptions(headers: headers, deadline: DateTime.UtcNow.Add(_options.ExportTimeout), cancellationToken: ct));
            var response = await call.ResponseAsync.ConfigureAwait(false);
            if (response.PartialSuccess is { RejectedItems: > 0 })
            {
                _logger.LogWarning(
                    "KamsoraAPM Agent: profile Collector partial-success — {Rejected} rejected: {Msg}",
                    response.PartialSuccess.RejectedItems, response.PartialSuccess.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM Agent: profile export to {Endpoint} failed. PprofBytes={Bytes} SampleCount={Count}.",
                _options.Endpoint, capture.PprofBytes.Length, capture.SampleCount);
        }
    }

    private static ProtoResource BuildResource(KamsoraApmOptions options)
    {
        var resource = new ProtoResource();
        resource.Attributes.Add(KvOf("service.name", options.ServiceName));
        if (!string.IsNullOrEmpty(options.ServiceNamespace))
            resource.Attributes.Add(KvOf("service.namespace", options.ServiceNamespace));
        resource.Attributes.Add(KvOf("service.version",
            options.ServiceVersion ?? typeof(KamsoraApmProfileExporter).Assembly.GetName().Version?.ToString() ?? "0.0.0"));
        resource.Attributes.Add(KvOf("kamsora.agent.version", KamsoraApmAgent.Version));
        resource.Attributes.Add(KvOf("host.name", Environment.MachineName));
        foreach (var (k, v) in options.ResourceAttributes)
            resource.Attributes.Add(KvOf(k, v));
        return resource;
    }

    private static ProtoKv KvOf(string key, string value) =>
        new() { Key = key, Value = new ProtoAnyValue { StringValue = value ?? string.Empty } };

    private static ulong ToUnixNanos(DateTime utc)
    {
        const long UnixEpochTicks = 621_355_968_000_000_000L;
        var ticks = utc.Kind == DateTimeKind.Utc ? utc.Ticks : utc.ToUniversalTime().Ticks;
        var delta = ticks - UnixEpochTicks;
        return delta <= 0 ? 0UL : (ulong)delta * 100UL;
    }

    public void Dispose()
    {
        try { _grpcChannel.ShutdownAsync().GetAwaiter().GetResult(); } catch { /* swallow */ }
        _grpcChannel.Dispose();
    }
}

/// <summary>One captured CPU profile, ready to be shipped.</summary>
internal sealed record ProfileCapture(
    DateTime StartUtc,
    TimeSpan Duration,
    ProtoKind Kind,
    long SampleCount,
    byte[] PprofBytes,
    byte[]? TriggerTraceId);
