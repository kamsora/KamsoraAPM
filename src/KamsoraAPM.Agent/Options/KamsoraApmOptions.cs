// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel.DataAnnotations;

namespace KamsoraAPM.Agent.Options;

/// <summary>
/// Configuration for the in-process KamsoraAPM Agent. Bound from
/// <c>KamsoraApm</c> in <c>IConfiguration</c> or supplied via the
/// <c>AddKamsoraApm</c> callback.
/// </summary>
public sealed class KamsoraApmOptions
{
    /// <summary>
    /// gRPC endpoint of the KamsoraAPM Collector, e.g. <c>http://localhost:5080</c>.
    /// </summary>
    [Required]
    public Uri Endpoint { get; set; } = null!;

    /// <summary>
    /// Tenant UUID, sent in the <c>x-kamsora-tenant</c> gRPC metadata header.
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Cleartext API key for this tenant, sent in the <c>x-kamsora-api-key</c>
    /// gRPC metadata header. The Collector compares against the bcrypt hash
    /// stored in <c>masterapi_keys.key_hash</c>.
    /// </summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Logical service name (e.g. <c>checkout-api</c>). Maps to the
    /// <c>service.name</c> OTLP resource attribute.
    /// </summary>
    [Required]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Optional logical namespace (e.g. <c>shop</c>) for grouping related services.
    /// </summary>
    public string? ServiceNamespace { get; set; }

    /// <summary>Service version (e.g. <c>1.4.7</c>). Defaults to entry assembly version.</summary>
    public string? ServiceVersion { get; set; }

    /// <summary>
    /// Additional resource attributes carried on every emitted Span.
    /// Keys conventionally use OTLP semantic conventions
    /// (<c>deployment.environment</c>, <c>host.name</c>, …).
    /// </summary>
    public IDictionary<string, string> ResourceAttributes { get; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Bounded capacity of the in-process telemetry channel. When full, the
    /// Agent <b>drops</b> new items rather than blocking the application
    /// thread - this preserves the &lt; 2 ms overhead invariant under load.
    /// Defaults to 10,000.
    /// </summary>
    [Range(100, 1_000_000)]
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>
    /// Maximum span count flushed in a single gRPC <c>Export</c> call.
    /// Defaults to 512.
    /// </summary>
    [Range(1, 10_000)]
    public int MaxExportBatchSize { get; set; } = 512;

    /// <summary>
    /// Maximum time the flusher waits before sending a partial batch.
    /// Defaults to 1 second.
    /// </summary>
    public TimeSpan ExportInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Per-attempt gRPC export timeout. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Enable Agent self-diagnostic logging. Verbose; off by default.
    /// </summary>
    public bool EnableDiagnostics { get; set; }

    /// <summary>
    /// Activity-source name prefixes to capture. <b>Empty (the default) means the
    /// Agent listens to EVERY <c>ActivitySource</c></b>, so any OTel-emitting
    /// library - HTTP, SQL and NoSQL drivers, cache clients, message queues,
    /// gRPC, and your own custom sources - is captured automatically with no
    /// per-app configuration. Set one or more prefixes to restrict capture
    /// (e.g. <c>"Npgsql"</c>, <c>"Microsoft.AspNetCore"</c>). The
    /// <see cref="CaptureKinds"/> filter still applies on top of this.
    /// </summary>
    public IList<string> CaptureSources { get; } = new List<string>();

    /// <summary>
    /// Activity kinds to forward. Defaults to all kinds - Server, Client,
    /// Internal, Producer, Consumer. Reduce this if you find Internal
    /// activities are noisy.
    /// </summary>
    public IList<string> CaptureKinds { get; } = new List<string>
    {
        "Server", "Client", "Internal", "Producer", "Consumer",
    };

    /// <summary>
    /// M6 consumer analytics: how the Agent identifies the consumer of an
    /// inbound HTTP request. Defaults to <see cref="ConsumerExtractorType.JwtClaim"/>
    /// with claim <c>sub</c> and IP fallback - works out of the box for the
    /// typical ASP.NET Core API with JWT bearer auth.
    /// </summary>
    public ConsumerExtractorOptions ConsumerExtractor { get; set; } = new();

    /// <summary>
    /// M8 - enable OTel log capture (Microsoft.Extensions.Logging → KamsoraAPM
    /// Collector). When true, every <c>ILogger</c> call emitted by the host
    /// app is exported with trace context. Defaults to <c>true</c>; set to
    /// <c>false</c> to opt out (volume control / PII concerns).
    /// </summary>
    public bool EnableLogs { get; set; } = true;

    /// <summary>
    /// M8 - enable OTel metrics capture. Captures counters, gauges, and
    /// histograms emitted via <c>System.Diagnostics.Metrics.Meter</c> in app
    /// code plus ASP.NET Core / Runtime built-in meters. Defaults to <c>true</c>.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Meter name patterns the Agent listens to. Empty list = listen to all.
    /// Defaults pull in <c>Microsoft.AspNetCore.Hosting</c> request metrics,
    /// runtime GC metrics, and any <c>Kamsora.*</c> custom meters the app
    /// emits.
    /// </summary>
    public IList<string> MetricSources { get; } = new List<string>
    {
        "Microsoft.AspNetCore.*",
        "System.Runtime",
        "Kamsora.*",
    };

    /// <summary>Period for metric exports. Defaults to 30 seconds - same as OTel's default reader.</summary>
    public TimeSpan MetricExportInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// M9 - enable continuous CPU profiling. The Agent self-connects to the
    /// runtime diagnostic port via EventPipe and captures a short CPU
    /// sampling profile every <see cref="ProfilingInterval"/>, then ships
    /// it as pprof to the KamsoraAPM Collector.
    ///
    /// <para>
    /// <b>Defaults to <c>false</c></b> as of v1.3.1 - the EventPipe self-connect
    /// path can interact badly with some host configurations (named-pipe
    /// races, ThreadPool contention with the SampleProfiler provider).
    /// Opt in explicitly via <c>KamsoraApm:EnableProfiling = true</c> in
    /// config once you've validated it on a non-prod copy of the service.
    /// </para>
    /// </summary>
    public bool EnableProfiling { get; set; }

    /// <summary>How often to start a new profiling session. Defaults to 60 s.</summary>
    public TimeSpan ProfilingInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long each profiling session captures samples. Defaults to 10 s.</summary>
    public TimeSpan ProfilingDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// M10 - trace head-sampling ratio in <c>[0.0, 1.0]</c>. <c>1.0</c> (the
    /// default) captures every trace. <c>0.1</c> captures ~10% of root traces
    /// deterministically by trace-id hash, so every span belonging to the
    /// sampled trace is kept and every span of the unsampled trace is dropped
    /// - no orphaned children. The decision respects an upstream parent's
    /// already-set sample flag so propagation across services stays coherent.
    ///
    /// <para>
    /// <b>Known caveat:</b> dashboard statistics derived from spans
    /// (Overview request counts, Services error rates, Consumers analytics)
    /// reflect only the SAMPLED traces - at 0.1 they show ~10% of true
    /// volume. OTel metrics (http.server.request.duration etc. on the
    /// Metrics page) are unaffected because the metrics pipeline is never
    /// sampled. Sampled-count upscaling is tracked for a future release;
    /// until then prefer the Metrics page for true request volumes when
    /// sampling is enabled.
    /// </para>
    /// </summary>
    [Range(0.0, 1.0)]
    public double TraceSampleRatio { get; set; } = 1.0;
}

/// <summary>Selects which <c>IConsumerExtractor</c> implementation the Agent uses.</summary>
public enum ConsumerExtractorType
{
    /// <summary>Disable consumer tagging - every span gets an empty consumer id.</summary>
    None     = 0,
    /// <summary>Read a claim from the request's authenticated user / bearer JWT.</summary>
    JwtClaim = 1,
    /// <summary>Read a single request header.</summary>
    Header   = 2,
    /// <summary>Use the client IP (honors <c>X-Forwarded-For</c>).</summary>
    ClientIp = 3,
}

/// <summary>Configuration for the M6 consumer extractor.</summary>
public sealed class ConsumerExtractorOptions
{
    /// <summary>Which extractor to use. Defaults to <see cref="ConsumerExtractorType.JwtClaim"/>.</summary>
    public ConsumerExtractorType Type { get; set; } = ConsumerExtractorType.JwtClaim;

    /// <summary>JWT claim name (used when <see cref="Type"/> = <see cref="ConsumerExtractorType.JwtClaim"/>). Defaults to <c>sub</c>.</summary>
    public string? ClaimName { get; set; } = "sub";

    /// <summary>Header name (used when <see cref="Type"/> = <see cref="ConsumerExtractorType.Header"/>). Defaults to <c>X-API-Consumer</c>.</summary>
    public string? HeaderName { get; set; } = "X-API-Consumer";

    /// <summary>
    /// When the primary extractor returns null, fall back to the client IP.
    /// Set false to leave anonymous requests un-attributed.
    /// </summary>
    public bool FallbackToClientIp { get; set; } = true;
}
