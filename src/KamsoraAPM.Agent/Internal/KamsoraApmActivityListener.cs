// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using KamsoraAPM.Agent.Options;
using KamsoraAPM.Contracts.Trace.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Agent.Internal;

/// <summary>
/// Subscribes to the ASP.NET Core <c>ActivitySource</c> and pushes a Kamsora
/// <see cref="Span"/> onto the export channel on each completed server activity.
///
/// Why a listener rather than a middleware?
///  - No <c>UseKamsoraApm()</c> required in user code.
///  - Captures activities from any participating SDK (HttpClient, EF, …)
///    once we widen the <c>ShouldListenTo</c> predicate in M2.
///  - Zero impact when the framework hasn't started the activity (e.g. when
///    the user has disabled the built-in HTTP-server activity).
/// </summary>
internal sealed class KamsoraApmActivityListener : IDisposable
{
    private static readonly string[] DefaultCapturePrefixes =
    {
        "Microsoft.AspNetCore",          // HTTP server requests
        "System.Net.Http",               // outbound HttpClient
        "Microsoft.EntityFrameworkCore", // EF Core queries (DbCommand events on EF 6+)
        "Npgsql",                        // PostgreSQL self-emitted by Npgsql 6+
        "MySqlConnector",                // MySQL self-emitted by MySqlConnector 2+
        "OracleDataProvider",            // Oracle ODP.NET 23ai+
        "OpenTelemetry.Instrumentation", // any OTel-shipped instrumentation we register
        "Kamsora.",                      // user-defined sources following our convention
    };

    private readonly ChannelWriter<Span> _channelWriter;
    private readonly ILogger<KamsoraApmActivityListener> _logger;
    private readonly KamsoraApmOptions _options;
    private readonly ActivityListener _listener;
    private readonly HashSet<ActivityKind> _captureKinds;
    private readonly string[] _capturePrefixes;
    private readonly ulong _sampleThreshold;     // 0 = drop all, ulong.MaxValue = keep all
    private long _droppedSpans;

    public KamsoraApmActivityListener(
        ChannelWriter<Span> channelWriter,
        IOptions<KamsoraApmOptions> options,
        ILogger<KamsoraApmActivityListener> logger)
    {
        _channelWriter = channelWriter;
        _options       = options.Value;
        _logger        = logger;

        _capturePrefixes = _options.CaptureSources.Count > 0
            ? _options.CaptureSources.ToArray()
            : DefaultCapturePrefixes;

        _captureKinds = new HashSet<ActivityKind>(
            _options.CaptureKinds.Select(ParseKind).Where(k => k.HasValue).Select(k => k!.Value));

        // Pre-compute the sampling threshold so the per-span Sample callback
        // is a single 64-bit compare. ratio=1.0 -> ulong.MaxValue (always keep);
        // ratio=0.0 -> 0 (always drop); intermediate values scale linearly.
        var ratio = Math.Clamp(_options.TraceSampleRatio, 0.0, 1.0);
        _sampleThreshold = ratio >= 1.0
            ? ulong.MaxValue
            : ratio <= 0.0
                ? 0UL
                : (ulong)(ratio * ulong.MaxValue);

        _listener = new ActivityListener
        {
            ShouldListenTo  = ShouldListenTo,
            Sample          = SampleByRatio,
            SampleUsingParentId = SampleByParentId,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(_listener);

        if (_options.EnableDiagnostics)
        {
            _logger.LogInformation(
                "KamsoraAPM Agent ActivityListener subscribed (service={ServiceName}, tenant={TenantId}, sources=[{Sources}], kinds=[{Kinds}])",
                _options.ServiceName, _options.TenantId,
                string.Join(", ", _capturePrefixes),
                string.Join(", ", _captureKinds));
        }
    }

    /// <summary>Total spans dropped because the channel was full. Exposed for diagnostics + tests.</summary>
    public long DroppedSpans => Interlocked.Read(ref _droppedSpans);

    private bool ShouldListenTo(ActivitySource source)
    {
        for (int i = 0; i < _capturePrefixes.Length; i++)
        {
            if (source.Name.StartsWith(_capturePrefixes[i], StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private ActivitySamplingResult SampleByRatio(ref ActivityCreationOptions<ActivityContext> options)
    {
        // Honour an upstream parent's sample decision so cross-service
        // traces stay coherent: if the caller already marked the trace
        // as recorded, we follow; if it stripped the recorded flag we
        // skip the data but still propagate.
        if (options.Parent.TraceId != default)
        {
            return (options.Parent.TraceFlags & ActivityTraceFlags.Recorded) != 0
                ? ActivitySamplingResult.AllDataAndRecorded
                : ActivitySamplingResult.PropagationData;
        }

        // Root span - deterministic hash of the trace id picks the same
        // outcome every time the trace is observed, so every span belonging
        // to the trace is folded into the same keep-or-drop decision.
        if (_sampleThreshold == ulong.MaxValue) return ActivitySamplingResult.AllDataAndRecorded;
        if (_sampleThreshold == 0UL)            return ActivitySamplingResult.PropagationData;

        Span<byte> bytes = stackalloc byte[16];
        options.TraceId.CopyTo(bytes);
        var hash = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        return hash < _sampleThreshold
            ? ActivitySamplingResult.AllDataAndRecorded
            : ActivitySamplingResult.PropagationData;
    }

    private ActivitySamplingResult SampleByParentId(ref ActivityCreationOptions<string> options)
    {
        // Activities started by parent-id (legacy W3C TraceContext format)
        // can't be ratio-sampled here because we don't have the parent's
        // trace-flag - fall through to keeping the span.
        return ActivitySamplingResult.AllDataAndRecorded;
    }

    private static ActivityKind? ParseKind(string s) => s?.Trim() switch
    {
        "Server"   => ActivityKind.Server,
        "Client"   => ActivityKind.Client,
        "Internal" => ActivityKind.Internal,
        "Producer" => ActivityKind.Producer,
        "Consumer" => ActivityKind.Consumer,
        _          => null,
    };

    private void OnActivityStopped(Activity activity)
    {
        if (!_captureKinds.Contains(activity.Kind)) return;

        Span span;
        try
        {
            span = SpanFactory.FromActivity(activity);
        }
        catch (Exception ex)
        {
            // Failing to build the proto must never propagate to the host app.
            _logger.LogWarning(ex, "KamsoraAPM Agent failed to convert Activity to Span (id={SpanId})", activity.SpanId);
            return;
        }

        if (!_channelWriter.TryWrite(span))
        {
            Interlocked.Increment(ref _droppedSpans);
            if (_options.EnableDiagnostics)
            {
                _logger.LogDebug("KamsoraAPM Agent dropped span (queue full).");
            }
        }
    }

    public void Dispose() => _listener.Dispose();
}
