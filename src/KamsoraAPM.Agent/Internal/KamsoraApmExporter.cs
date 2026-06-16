// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using Grpc.Core;
using Grpc.Net.Client;
using KamsoraAPM.Agent.Options;
using KamsoraAPM.Contracts.Collector.V1;
using KamsoraAPM.Contracts.Common.V1;
using KamsoraAPM.Contracts.Trace.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace KamsoraAPM.Agent.Internal;

/// <summary>
/// gRPC client wrapper used by the background flusher to export batches of
/// spans to the Collector. Builds the <see cref="ResourceSpans"/> envelope
/// once per export call and attaches tenant credentials to every call's
/// gRPC metadata.
/// </summary>
internal sealed class KamsoraApmExporter : IAsyncDisposable
{
    private readonly KamsoraApmOptions _options;
    private readonly ILogger<KamsoraApmExporter> _logger;
    private readonly GrpcChannel _grpcChannel;
    private readonly TraceService.TraceServiceClient _traceClient;
    private readonly Resource _resource;
    private readonly ResiliencePipeline _retryPipeline;

    public KamsoraApmExporter(IOptions<KamsoraApmOptions> options, ILogger<KamsoraApmExporter> logger)
    {
        _options = options.Value;
        _logger  = logger;

        _grpcChannel = KamsoraGrpcChannelFactory.Create(_options);
        _traceClient = new TraceService.TraceServiceClient(_grpcChannel);
        _resource    = BuildResource(_options);

        // Polly v8 pipeline: exponential back-off with jitter, capped at 5 retries.
        // We deliberately do not retry on Cancelled / DeadlineExceeded - those are
        // either shutdown or the Collector being slow; in both cases the channel
        // back-pressure already protects us.
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts  = 5,
                BackoffType       = DelayBackoffType.Exponential,
                UseJitter         = true,
                Delay             = TimeSpan.FromMilliseconds(200),
                MaxDelay          = TimeSpan.FromSeconds(5),
                ShouldHandle      = static args => args.Outcome.Exception switch
                {
                    RpcException rpc when IsTransient(rpc) => PredicateResult.True(),
                    HttpRequestException => PredicateResult.True(),
                    _ => PredicateResult.False(),
                },
            })
            .Build();
    }

    /// <summary>
    /// Export a batch of spans. Returns true if the batch was accepted
    /// (including partial-success - the server returns 200 with rejected_count &gt; 0).
    /// </summary>
    public async ValueTask<bool> ExportAsync(IReadOnlyList<Span> spans, CancellationToken cancellationToken)
    {
        if (spans.Count == 0) return true;

        var request = new ExportTraceRequest();
        var scope   = new ScopeSpans
        {
            Scope = new InstrumentationScope
            {
                Name    = "KamsoraAPM.Agent",
                Version = KamsoraApmAgent.Version,
            },
        };
        scope.Spans.AddRange(spans);

        var resourceSpans = new ResourceSpans { Resource = _resource };
        resourceSpans.ScopeSpans.Add(scope);
        request.ResourceSpans.Add(resourceSpans);

        var headers = KamsoraGrpcChannelFactory.BuildAuthMetadata(_options);

        // Keep our own export RPC out of the captured telemetry.
        using var suppressSelfTrace = AgentSelfTrace.Suppress();
        try
        {
            await _retryPipeline.ExecuteAsync(async ct =>
            {
                using var call = _traceClient.ExportAsync(
                    request,
                    new CallOptions(headers: headers, deadline: DateTime.UtcNow.Add(_options.ExportTimeout), cancellationToken: ct));
                var response = await call.ResponseAsync.ConfigureAwait(false);

                if (response.PartialSuccess is { RejectedItems: > 0 })
                {
                    _logger.LogWarning(
                        "KamsoraAPM Agent: Collector partial-success - {RejectedItems} span(s) rejected: {Message}",
                        response.PartialSuccess.RejectedItems, response.PartialSuccess.ErrorMessage);
                }
            }, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown - do not log as error.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "KamsoraAPM Agent: span export to {Endpoint} failed after retries. Batch size {Count}.",
                _options.Endpoint, spans.Count);
            return false;
        }
    }

    private static bool IsTransient(RpcException rpc) => rpc.StatusCode switch
    {
        StatusCode.Unavailable      => true,
        StatusCode.DeadlineExceeded => false,    // caller's deadline
        StatusCode.ResourceExhausted=> true,
        StatusCode.Aborted          => true,
        StatusCode.Internal         => true,
        _                           => false,
    };

    private static Resource BuildResource(KamsoraApmOptions options)
    {
        var resource = new Resource();
        resource.Attributes.Add(KeyValueOf("service.name", options.ServiceName));
        if (!string.IsNullOrEmpty(options.ServiceNamespace))
            resource.Attributes.Add(KeyValueOf("service.namespace", options.ServiceNamespace));
        resource.Attributes.Add(KeyValueOf("service.version",
            options.ServiceVersion ?? typeof(KamsoraApmExporter).Assembly.GetName().Version?.ToString() ?? "0.0.0"));
        resource.Attributes.Add(KeyValueOf("kamsora.agent.version", KamsoraApmAgent.Version));
        resource.Attributes.Add(KeyValueOf("host.name", Environment.MachineName));
        resource.Attributes.Add(KeyValueOf("os.type",   Environment.OSVersion.Platform.ToString()));

        foreach (var (k, v) in options.ResourceAttributes)
        {
            resource.Attributes.Add(KeyValueOf(k, v));
        }
        return resource;
    }

    private static KeyValue KeyValueOf(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value ?? string.Empty } };

    public async ValueTask DisposeAsync()
    {
        await _grpcChannel.ShutdownAsync().ConfigureAwait(false);
        _grpcChannel.Dispose();
    }
}
