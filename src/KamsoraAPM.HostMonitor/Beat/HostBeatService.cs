// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using Google.Protobuf;
using Grpc.Core;
using KamsoraAPM.Contracts.Collector.V1;
using KamsoraAPM.Contracts.Common.V1;
using KamsoraAPM.Contracts.Host.V1;
using KamsoraAPM.HostMonitor.Options;
using KamsoraAPM.HostMonitor.Sampling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace KamsoraAPM.HostMonitor.Beat;

/// <summary>
/// Long-running worker that samples CPU + memory on a fixed cadence, builds an
/// OTLP-shaped <see cref="HostSnapshot"/>, buffers it, and pushes batches to the
/// Collector's <c>HostService.Export</c> gRPC endpoint. Auth headers
/// (<c>x-kamsora-tenant</c>, <c>x-kamsora-api-key</c>) are stamped on every call.
/// </summary>
internal sealed class HostBeatService : BackgroundService
{
    private readonly ICpuMemorySampler _cpuMemorySampler;
    private readonly IDiskSampler      _diskSampler;
    private readonly INetworkSampler   _networkSampler;
    private readonly IProcessSampler   _processSampler;
    private readonly HostService.HostServiceClient _client;
    private readonly HostMonitorOptions _options;
    private readonly ILogger<HostBeatService> _logger;
    private readonly Resource _resource;
    private readonly Metadata _authHeaders;
    private readonly ResiliencePipeline _retry;

    public HostBeatService(
        ICpuMemorySampler cpuMemorySampler,
        IDiskSampler      diskSampler,
        INetworkSampler   networkSampler,
        IProcessSampler   processSampler,
        HostService.HostServiceClient client,
        IOptions<HostMonitorOptions> options,
        ILogger<HostBeatService> logger)
    {
        _cpuMemorySampler = cpuMemorySampler;
        _diskSampler      = diskSampler;
        _networkSampler   = networkSampler;
        _processSampler   = processSampler;
        _client  = client;
        _options = options.Value;
        _logger  = logger;

        var hostId   = HostIdentity.Resolve(_options.HostIdOverride);
        var hostName = string.IsNullOrWhiteSpace(_options.HostName)
            ? Environment.MachineName
            : _options.HostName;

        _resource = new Resource
        {
            Attributes =
            {
                MakeKv("host.id",    hostId),
                MakeKv("host.name",  hostName),
                MakeKv("os.type",    HostIdentity.GetOsType()),
                MakeKv("os.version", HostIdentity.GetOsVersion()),
            },
        };

        _authHeaders = new Metadata
        {
            { "x-kamsora-tenant",  _options.TenantId },
            { "x-kamsora-api-key", _options.ApiKey   },
        };

        _retry = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType      = DelayBackoffType.Exponential,
                UseJitter        = true,
                Delay            = TimeSpan.FromSeconds(1),
                MaxDelay         = TimeSpan.FromSeconds(15),
            })
            .Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "KamsoraAPM HostMonitor started. tenant={Tenant} host_id={HostId} collector={Endpoint} interval={Interval}s",
            _options.TenantId, GetHostIdFromResource(), _options.CollectorEndpoint, _options.CpuMemoryInterval.TotalSeconds);

        var buffer = new List<HostSnapshot>(_options.MaxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (cpu, memory) = await _cpuMemorySampler.SampleAsync(stoppingToken).ConfigureAwait(false);
                var disks         = await _diskSampler     .SampleAsync(stoppingToken).ConfigureAwait(false);
                var networks      = await _networkSampler  .SampleAsync(stoppingToken).ConfigureAwait(false);
                var processes     = await _processSampler  .SampleAsync(stoppingToken).ConfigureAwait(false);

                var snapshot = new HostSnapshot
                {
                    Resource     = _resource,
                    TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL,
                    Cpu          = cpu,
                    Memory       = memory,
                };
                snapshot.Disks.AddRange(disks);
                snapshot.Networks.AddRange(networks);
                snapshot.Processes.AddRange(processes);
                buffer.Add(snapshot);

                if (buffer.Count >= _options.MaxBatchSize)
                {
                    await ExportAsync(buffer, stoppingToken).ConfigureAwait(false);
                    buffer.Clear();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "KamsoraAPM HostMonitor: sample/export iteration failed; continuing.");
            }

            try
            {
                await Task.Delay(_options.CpuMemoryInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Best-effort final flush so the last partial batch reaches the Collector.
        if (buffer.Count > 0)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await ExportAsync(buffer, deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "KamsoraAPM HostMonitor: final flush dropped {Count} snapshot(s).", buffer.Count); }
        }

        _logger.LogInformation("KamsoraAPM HostMonitor stopped.");
    }

    private async Task ExportAsync(List<HostSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var request = new ExportHostRequest();
        request.Snapshots.AddRange(snapshots);

        await _retry.ExecuteAsync(async ct =>
        {
            var call = _client.ExportAsync(request, _authHeaders, deadline: null, cancellationToken: ct);
            await call.ResponseAsync.ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("KamsoraAPM HostMonitor: exported {Count} host snapshot(s).", snapshots.Count);
    }

    private string GetHostIdFromResource()
    {
        foreach (var kv in _resource.Attributes)
            if (kv.Key == "host.id") return kv.Value.StringValue;
        return string.Empty;
    }

    private static KeyValue MakeKv(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value ?? string.Empty } };
}
