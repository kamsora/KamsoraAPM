// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Channels;
using KamsoraAPM.Collector.Infrastructure;
using KamsoraAPM.Collector.Ingestion;
using KamsoraAPM.Collector.Options;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.ClickHouse;
using KamsoraAPM.Storage.Extensions;
using KamsoraAPM.Storage.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // M10/P0 - fail fast when Production still carries repo dev secrets.
    KamsoraAPM.Storage.Extensions.DevSecretsGuard.ThrowIfProductionWithDevSecrets(
        builder.Environment, builder.Configuration);

    builder.Host.UseSerilog((ctx, _, loggerConfig) =>
    {
        loggerConfig
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    });

    builder.Services.AddOptions<CollectorOptions>()
        .Bind(builder.Configuration.GetSection("KamsoraApm:Collector"))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddKamsoraStorage(builder.Configuration);

    // M10 - schema migrations apply at startup, BEFORE any flusher starts
    // (hosted services start sequentially in registration order, so this
    // must remain the first AddHostedService call).
    builder.Services.AddSingleton<ClickHouseMigrationRunner>();
    builder.Services.AddHostedService<MigrationHostedService>();

    // Bounded ingestion channel shared by KamsoraTraceService (producer) and the
    // SpanFlusherHostedService (consumer).
    builder.Services.TryAddSingleton<Channel<SpanRow>>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<CollectorOptions>>().Value;
        return Channel.CreateBounded<SpanRow>(new BoundedChannelOptions(opts.QueueCapacity)
        {
            FullMode               = BoundedChannelFullMode.DropWrite,
            SingleReader           = true,
            SingleWriter           = false,
            AllowSynchronousContinuations = false,
        });
    });
    builder.Services.TryAddSingleton<ChannelReader<SpanRow>>(sp => sp.GetRequiredService<Channel<SpanRow>>().Reader);
    builder.Services.TryAddSingleton<ChannelWriter<SpanRow>>(sp => sp.GetRequiredService<Channel<SpanRow>>().Writer);

    // One bounded channel per host table. Producer is KamsoraHostService; each
    // consumer is a typed BatchFlusherHostedService<TRow>. Channels are
    // sized off QueueCapacity (overkill for host volumes but reuses the knob).
    RegisterHostChannel<HostCpuMemoryRow>(builder.Services);
    RegisterHostChannel<HostDiskRow>     (builder.Services);
    RegisterHostChannel<HostNetworkRow>  (builder.Services);
    RegisterHostChannel<HostProcessRow>  (builder.Services);

    // M8 - log + metric ingest channels.
    RegisterHostChannel<LogRow>         (builder.Services);
    RegisterHostChannel<MetricPointRow> (builder.Services);

    // M9 - profile ingest channel. Profiles are bigger than spans (10 KB
    // pprof vs ~1 KB span) and far less frequent (one per minute per service
    // vs hundreds per second), so the channel sits at the same QueueCapacity
    // but realistically holds at most a handful of rows.
    RegisterHostChannel<ProfileRow>     (builder.Services);

    builder.Services.AddSingleton<AuthInterceptor>();
    builder.Services.AddSingleton<MetricCardinalityGuard>();
    builder.Services.AddSingleton<KamsoraTraceService>();
    builder.Services.AddSingleton<KamsoraHostService>();
    builder.Services.AddSingleton<KamsoraLogsService>();
    builder.Services.AddSingleton<KamsoraMetricsService>();
    builder.Services.AddSingleton<KamsoraProfilesService>();

    // M10/P1 - standard OTLP/gRPC ingestion. Lets ANY OpenTelemetry SDK
    // (Python, Node, Java, Go, …) export to KamsoraAPM with just an
    // endpoint + headers change. Reuses the same mappers + channels.
    builder.Services.AddSingleton<OtlpTraceService>();
    builder.Services.AddSingleton<OtlpLogsService>();
    builder.Services.AddSingleton<OtlpMetricsService>();

    static void RegisterHostChannel<TRow>(IServiceCollection services) where TRow : class
    {
        services.TryAddSingleton<Channel<TRow>>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CollectorOptions>>().Value;
            return Channel.CreateBounded<TRow>(new BoundedChannelOptions(opts.QueueCapacity)
            {
                FullMode               = BoundedChannelFullMode.DropWrite,
                SingleReader           = true,
                SingleWriter           = false,
                AllowSynchronousContinuations = false,
            });
        });
        services.TryAddSingleton<ChannelReader<TRow>>(sp => sp.GetRequiredService<Channel<TRow>>().Reader);
        services.TryAddSingleton<ChannelWriter<TRow>>(sp => sp.GetRequiredService<Channel<TRow>>().Writer);
    }

    builder.Services.AddGrpc(o =>
    {
        // Auth interceptor wired solution-wide; every gRPC service requires a tenant.
        o.Interceptors.Add<AuthInterceptor>();
        // M10 - hard cap on inbound message size. The biggest legitimate
        // payload is a profile batch (~1 MB after compression); 16 MB leaves
        // generous headroom while stopping a buggy or hostile client from
        // streaming a gigabyte into Collector memory.
        o.MaxReceiveMessageSize = 16 * 1024 * 1024;
        // Accept gzip-compressed requests from v1.3.2+ Agents.
        o.ResponseCompressionAlgorithm = "gzip";
    });

    // M10 - readiness now actually pings both stores.
    builder.Services.AddHealthChecks()
        .AddCheck<ClickHouseHealthCheck>("clickhouse")
        .AddCheck<PostgresHealthCheck>("postgres");

    // M10 - per-tenant retention enforcement via partition drops.
    builder.Services.AddHostedService<RetentionSweeperHostedService>();

    builder.Services.AddHostedService<SpanFlusherHostedService>();
    AddHostFlusher<HostCpuMemoryRow>(builder.Services, (w, rows, ct) => w.WriteCpuMemoryAsync(rows, ct));
    AddHostFlusher<HostDiskRow>     (builder.Services, (w, rows, ct) => w.WriteDisksAsync    (rows, ct));
    AddHostFlusher<HostNetworkRow>  (builder.Services, (w, rows, ct) => w.WriteNetworksAsync (rows, ct));
    AddHostFlusher<HostProcessRow>  (builder.Services, (w, rows, ct) => w.WriteProcessesAsync(rows, ct));

    // M8 - independent batch flushers for logs + metrics.
    builder.Services.AddHostedService(sp => new BatchFlusherHostedService<LogRow>(
        sp.GetRequiredService<ChannelReader<LogRow>>(),
        (rows, ct) => sp.GetRequiredService<ILogWriter>().WriteAsync(rows, ct),
        sp.GetRequiredService<IOptions<CollectorOptions>>(),
        sp.GetRequiredService<ILogger<BatchFlusherHostedService<LogRow>>>()));

    builder.Services.AddHostedService(sp => new BatchFlusherHostedService<MetricPointRow>(
        sp.GetRequiredService<ChannelReader<MetricPointRow>>(),
        (rows, ct) => sp.GetRequiredService<IMetricWriter>().WriteAsync(rows, ct),
        sp.GetRequiredService<IOptions<CollectorOptions>>(),
        sp.GetRequiredService<ILogger<BatchFlusherHostedService<MetricPointRow>>>()));

    builder.Services.AddHostedService(sp => new BatchFlusherHostedService<ProfileRow>(
        sp.GetRequiredService<ChannelReader<ProfileRow>>(),
        (rows, ct) => sp.GetRequiredService<IProfileWriter>().WriteAsync(rows, ct),
        sp.GetRequiredService<IOptions<CollectorOptions>>(),
        sp.GetRequiredService<ILogger<BatchFlusherHostedService<ProfileRow>>>()));

    static void AddHostFlusher<TRow>(
        IServiceCollection services,
        Func<IHostSnapshotWriter, IReadOnlyList<TRow>, CancellationToken, Task> writeMethod)
        where TRow : class
    {
        services.AddHostedService(sp =>
            new BatchFlusherHostedService<TRow>(
                sp.GetRequiredService<ChannelReader<TRow>>(),
                (rows, ct) => writeMethod(sp.GetRequiredService<IHostSnapshotWriter>(), rows, ct),
                sp.GetRequiredService<IOptions<CollectorOptions>>(),
                sp.GetRequiredService<ILogger<BatchFlusherHostedService<TRow>>>()));
    }

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.MapGet("/", () => Results.Text(
        "KamsoraAPM Collector\n" +
        "  gRPC ingestion:  POST /kamsora.collector.v1.TraceService/Export\n" +
        "  Health probe:    GET /healthz\n" +
        "  Readiness probe: GET /readyz\n"));

    // Liveness: process is up. Readiness: ClickHouse + Postgres reachable.
    app.MapGet("/healthz", () => Results.Ok(new { status = "ok", component = "kamsora-apm-collector" }));
    app.MapHealthChecks("/readyz");

    // M10 - self-observability. Channel depths + drop counters, JSON.
    app.MapGet("/stats", (
        ChannelReader<SpanRow> spanReader,
        ChannelReader<LogRow> logReader,
        ChannelReader<MetricPointRow> metricReader,
        ChannelReader<ProfileRow> profileReader,
        KamsoraTraceService traceSvc,
        KamsoraLogsService logsSvc,
        KamsoraMetricsService metricsSvc,
        KamsoraProfilesService profilesSvc,
        MetricCardinalityGuard cardinality) => Results.Ok(new
    {
        channels = new
        {
            spans    = spanReader.Count,
            logs     = logReader.Count,
            metrics  = metricReader.Count,
            profiles = profileReader.Count,
        },
        droppedSinceStart = new
        {
            spans    = traceSvc.DroppedRows,
            logs     = logsSvc.DroppedRows,
            metrics  = metricsSvc.DroppedRows,
            profiles = profilesSvc.DroppedRows,
            metricCardinalityRejected = cardinality.RejectedTotal,
        },
    }));

    app.MapGrpcService<KamsoraTraceService>();
    app.MapGrpcService<KamsoraHostService>();
    app.MapGrpcService<KamsoraLogsService>();
    app.MapGrpcService<KamsoraMetricsService>();
    app.MapGrpcService<KamsoraProfilesService>();

    // Standard OTLP/gRPC routes (opentelemetry.proto.collector.*.v1).
    app.MapGrpcService<OtlpTraceService>();
    app.MapGrpcService<OtlpLogsService>();
    app.MapGrpcService<OtlpMetricsService>();

    await app.RunAsync().ConfigureAwait(false);
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "KamsoraAPM Collector terminated unexpectedly.");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

// For WebApplicationFactory<Program> in tests.
namespace KamsoraAPM.Collector
{
    public partial class Program;
}
