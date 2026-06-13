// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel.DataAnnotations;
using System.Threading.Channels;
using KamsoraAPM.Agent.Internal;
using KamsoraAPM.Agent.Internal.ConsumerExtraction;
using KamsoraAPM.Agent.Internal.Profiling;
using KamsoraAPM.Agent.Options;
using KamsoraAPM.Contracts.Trace.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace KamsoraAPM.Agent.Extensions;

/// <summary>
/// DI extensions that wire the KamsoraAPM Agent into an ASP.NET Core host.
/// One call is enough — no <c>app.UseKamsoraApm()</c> required.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Default <c>IConfiguration</c> section bound by the
    /// <c>AddKamsoraApm(IConfiguration, ...)</c> overload.
    /// </summary>
    public const string ConfigurationSection = "KamsoraApm";

    /// <summary>
    /// Register the Agent with options supplied programmatically.
    /// </summary>
    public static IServiceCollection AddKamsoraApm(
        this IServiceCollection services,
        Action<KamsoraApmOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<KamsoraApmOptions>()
                .Configure(configure)
                .ValidateDataAnnotations()
                .Validate(static o => o.MaxExportBatchSize <= o.QueueCapacity,
                          "KamsoraApm: MaxExportBatchSize must be <= QueueCapacity.")
                .ValidateOnStart();

        return RegisterAgentServices(services);
    }

    /// <summary>
    /// Register the Agent binding <see cref="KamsoraApmOptions"/> from the
    /// <c>KamsoraApm</c> configuration section by default.
    /// </summary>
    public static IServiceCollection AddKamsoraApm(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = ConfigurationSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<KamsoraApmOptions>()
                .Bind(configuration.GetSection(sectionName))
                .ValidateDataAnnotations()
                .Validate(static o => o.MaxExportBatchSize <= o.QueueCapacity,
                          "KamsoraApm: MaxExportBatchSize must be <= QueueCapacity.")
                .ValidateOnStart();

        return RegisterAgentServices(services);
    }

    private static IServiceCollection RegisterAgentServices(IServiceCollection services)
    {
        // The bounded channel that backs the Agent's in-process telemetry buffer.
        services.TryAddSingleton<Channel<Span>>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<KamsoraApmOptions>>().Value;
            return Channel.CreateBounded<Span>(new BoundedChannelOptions(opts.QueueCapacity)
            {
                FullMode               = BoundedChannelFullMode.DropWrite,
                SingleReader           = true,
                SingleWriter           = false,
                AllowSynchronousContinuations = false,
            });
        });
        services.TryAddSingleton<ChannelReader<Span>>(sp => sp.GetRequiredService<Channel<Span>>().Reader);
        services.TryAddSingleton<ChannelWriter<Span>>(sp => sp.GetRequiredService<Channel<Span>>().Writer);

        services.TryAddSingleton<KamsoraApmExporter>();
        services.TryAddSingleton<KamsoraApmActivityListener>();

        // M6 consumer extractor — resolved from options at startup.
        services.TryAddSingleton<IConsumerExtractor>(sp =>
        {
            var ce = sp.GetRequiredService<IOptions<KamsoraApmOptions>>().Value.ConsumerExtractor;
            return ce.Type switch
            {
                ConsumerExtractorType.JwtClaim => new JwtClaimConsumerExtractor(ce),
                ConsumerExtractorType.Header   => new HeaderConsumerExtractor(ce),
                ConsumerExtractorType.ClientIp => new ClientIpConsumerExtractor(),
                _                              => new NullConsumerExtractor(),
            };
        });

        // The ActivityListener has no IDisposable lifetime tie-in via DI alone;
        // we hook it to the host via a tiny eager-activation hosted service.
        services.AddHostedService<ActivityListenerActivator>();
        services.AddHostedService<KamsoraApmExporterHostedService>();

        // Register OpenTelemetry instrumentations. These do NOT create activities;
        // they only ENRICH activities that ASP.NET Core and HttpClient already create.
        // Without them, the HTTP server activity has no http.method, http.route, etc.
        // The OTel TracerProvider has no exporter — we don't need one because our
        // ActivityListener already captures the activities and forwards them to the
        // KamsoraAPM Collector via gRPC.
        services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation(o =>
            {
                o.RecordException = true;          // capture exceptions as span events
                o.EnrichWithHttpRequest  = (activity, request) =>
                {
                    if (!string.IsNullOrEmpty(request.Headers.UserAgent.ToString()))
                        activity.SetTag("user_agent.original", request.Headers.UserAgent.ToString());

                    // M6 consumer analytics: resolve the extractor lazily off the
                    // HttpContext's request services — that gives us the same
                    // IConsumerExtractor singleton without capturing a global SP.
                    var extractor = request.HttpContext.RequestServices.GetService<IConsumerExtractor>();
                    var consumerId = extractor?.Extract(request);
                    if (!string.IsNullOrEmpty(consumerId))
                        activity.SetTag("kamsora.consumer.id", consumerId);
                };
                o.EnrichWithHttpResponse = (activity, response) =>
                {
                    // The default already sets http.response.status_code; nothing extra needed.
                };
            })
            .AddHttpClientInstrumentation(o =>
            {
                o.RecordException = true;
            })
            // SqlClient instrumentation for Microsoft.Data.SqlClient + System.Data.SqlClient.
            //   SetDbStatementForText = true means the actual SQL ends up in db.statement
            //   — extremely useful for debugging but can expose PII (literals in WHERE/INSERT).
            //   For a self-hosted observability tool the trade-off heavily favors visibility;
            //   users can disable per-deployment if PII becomes a concern.
            //
            // Other databases (PostgreSQL via Npgsql 6+, MySQL via MySqlConnector 2+, Oracle
            // ODP.NET 23ai+) emit their OWN ActivitySources directly. The KamsoraApmActivityListener
            // subscribes to those source names by default — no further registration needed.
            .AddSqlClientInstrumentation(o =>
            {
                o.SetDbStatementForText            = true;
                o.RecordException                  = true;
                o.EnableConnectionLevelAttributes  = true;
            }));

        // M8 — OTel logs + metrics. Both default to ON; users opt out via
        // KamsoraApm:EnableLogs / EnableMetrics in config.
        services.TryAddSingleton<KamsoraApmLogExporter>();
        services.TryAddSingleton<KamsoraApmMetricExporter>();

        // M9 — continuous CPU profiling. Off by default (see EnableProfiling
        // docs). The hosted service + exporter are only wired when the option
        // is explicitly true — that way the EventPipe + TraceEvent packages
        // never participate in the host's startup graph for the common case,
        // so any latent diagnostic-port quirks can't take the host down.
        services.AddSingleton<IHostedService>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<KamsoraApmOptions>>().Value;
            if (!opts.EnableProfiling)
                return new InertHostedService();
            var exporter = ActivatorUtilities.CreateInstance<KamsoraApmProfileExporter>(sp);
            return ActivatorUtilities.CreateInstance<KamsoraApmProfiler>(sp, exporter);
        });

        // WithLogging (3-arg overload) registers the ILoggerProvider AND wires
        // the SP-aware processor onto the same provider in one call. Avoids the
        // double-registration that previously left the SP-aware processor on an
        // orphan builder. Per OTel 1.12 docs:
        //   "This method automatically registers an ILoggerProvider named
        //    'OpenTelemetry' into the IServiceCollection."
        services.AddOpenTelemetry()
            .WithLogging(
                logging =>
                {
                    logging.AddProcessor(sp =>
                    {
                        var opts = sp.GetRequiredService<IOptions<KamsoraApmOptions>>().Value;
                        if (!opts.EnableLogs)
                            return new NoOpLogRecordProcessor();
                        var exporter = sp.GetRequiredService<KamsoraApmLogExporter>();
                        return new BatchLogRecordExportProcessor(exporter);
                    });
                },
                options =>
                {
                    options.IncludeFormattedMessage = true;
                    options.IncludeScopes           = true;
                })
            .WithMetrics(metrics =>
            {
                metrics.AddRuntimeInstrumentation();   // GC, threadpool, exceptions
                metrics.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Kamsora.*");
                metrics.AddReader(sp =>
                {
                    var opts = sp.GetRequiredService<IOptions<KamsoraApmOptions>>().Value;
                    if (!opts.EnableMetrics)
                        return new PeriodicExportingMetricReader(new NoOpMetricExporter(), exportIntervalMilliseconds: int.MaxValue);
                    var exporter = sp.GetRequiredService<KamsoraApmMetricExporter>();
                    return new PeriodicExportingMetricReader(exporter,
                        exportIntervalMilliseconds: (int)opts.MetricExportInterval.TotalMilliseconds);
                });
            });

        return services;
    }
}

/// <summary>No-op log processor used when EnableLogs = false.</summary>
internal sealed class NoOpLogRecordProcessor : BaseProcessor<OpenTelemetry.Logs.LogRecord> { }

/// <summary>No-op metric exporter used when EnableMetrics = false.</summary>
internal sealed class NoOpMetricExporter : BaseExporter<OpenTelemetry.Metrics.Metric>
{
    public override ExportResult Export(in Batch<OpenTelemetry.Metrics.Metric> batch) => ExportResult.Success;
}

/// <summary>
/// Placeholder hosted service. Registered in lieu of <c>KamsoraApmProfiler</c>
/// when <c>EnableProfiling = false</c> so the host's hosted-services collection
/// has zero references to the EventPipe / TraceEvent stack.
/// </summary>
internal sealed class InertHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync (CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Eagerly resolves the <see cref="KamsoraApmActivityListener"/> at host
/// start so that the underlying <c>ActivitySource.AddActivityListener</c>
/// subscription is active before the application receives its first request.
/// </summary>
internal sealed class ActivityListenerActivator : IHostedService
{
    private readonly KamsoraApmActivityListener _listener;

    public ActivityListenerActivator(KamsoraApmActivityListener listener) => _listener = listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = _listener; // touch
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener.Dispose();
        return Task.CompletedTask;
    }
}
