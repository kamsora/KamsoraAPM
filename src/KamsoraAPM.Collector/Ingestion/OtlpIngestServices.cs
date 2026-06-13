// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.
//
// Standard OTLP/gRPC ingestion services. Any OpenTelemetry SDK (Python,
// Node, Java, Go, .NET, …) can export straight to the KamsoraAPM Collector:
//
//   OTEL_EXPORTER_OTLP_ENDPOINT=http://collector:5080
//   OTEL_EXPORTER_OTLP_PROTOCOL=grpc
//   OTEL_EXPORTER_OTLP_HEADERS="x-kamsora-tenant=<uuid>,x-kamsora-api-key=<key>"
//
// The request messages reference the kamsora.* telemetry types directly
// (wire-compatible with upstream OTLP), so these handlers reuse the exact
// same row mappers, channels, and guards as the native Kamsora services.

using System.Threading.Channels;
using Grpc.Core;
using KamsoraAPM.Contracts.Otlp.Logs.V1;
using KamsoraAPM.Contracts.Otlp.Metrics.V1;
using KamsoraAPM.Contracts.Otlp.Trace.V1;
using KamsoraAPM.Storage.Models;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>OTLP <c>TraceService.Export</c> — the route every OTel SDK traces exporter calls.</summary>
public sealed class OtlpTraceService : TraceService.TraceServiceBase
{
    private readonly ChannelWriter<SpanRow> _writer;
    private readonly ILogger<OtlpTraceService> _logger;
    private long _droppedRows;

    public OtlpTraceService(ChannelWriter<SpanRow> writer, ILogger<OtlpTraceService> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    public long DroppedRows => Interlocked.Read(ref _droppedRows);

    public override Task<ExportTraceServiceResponse> Export(ExportTraceServiceRequest request, ServerCallContext context)
    {
        var tenant = TenantContextAccessor.GetTenant(context);
        long rejected = 0;

        foreach (var resourceSpans in request.ResourceSpans)
        {
            foreach (var scope in resourceSpans.ScopeSpans)
            {
                foreach (var span in scope.Spans)
                {
                    SpanRow row;
                    try
                    {
                        row = SpanRowMapper.ToRow(tenant.TenantId, resourceSpans, scope, span);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "KamsoraAPM Collector: failed to map OTLP span (tenant={Tenant}); rejected.", tenant.TenantId);
                        rejected++;
                        continue;
                    }

                    if (!_writer.TryWrite(row))
                    {
                        rejected++;
                        Interlocked.Increment(ref _droppedRows);
                    }
                }
            }
        }

        var response = new ExportTraceServiceResponse();
        if (rejected > 0)
        {
            response.PartialSuccess = new ExportTracePartialSuccess
            {
                RejectedSpans = rejected,
                ErrorMessage  = "KamsoraAPM Collector: ingestion buffer full or span mapping failed.",
            };
        }
        return Task.FromResult(response);
    }
}

/// <summary>OTLP <c>LogsService.Export</c>.</summary>
public sealed class OtlpLogsService : LogsService.LogsServiceBase
{
    private readonly ChannelWriter<LogRow> _writer;
    private readonly ILogger<OtlpLogsService> _logger;
    private long _droppedRows;

    public OtlpLogsService(ChannelWriter<LogRow> writer, ILogger<OtlpLogsService> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    public long DroppedRows => Interlocked.Read(ref _droppedRows);

    public override Task<ExportLogsServiceResponse> Export(ExportLogsServiceRequest request, ServerCallContext context)
    {
        var tenant = TenantContextAccessor.GetTenant(context);
        long rejected = 0;

        foreach (var resourceLogs in request.ResourceLogs)
        {
            foreach (var scope in resourceLogs.ScopeLogs)
            {
                foreach (var record in scope.LogRecords)
                {
                    LogRow row;
                    try
                    {
                        row = LogRowMapper.ToRow(tenant.TenantId, resourceLogs, scope, record);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "KamsoraAPM Collector: failed to map OTLP log (tenant={Tenant}); rejected.", tenant.TenantId);
                        rejected++;
                        continue;
                    }

                    if (!_writer.TryWrite(row))
                    {
                        rejected++;
                        Interlocked.Increment(ref _droppedRows);
                    }
                }
            }
        }

        var response = new ExportLogsServiceResponse();
        if (rejected > 0)
        {
            response.PartialSuccess = new ExportLogsPartialSuccess
            {
                RejectedLogRecords = rejected,
                ErrorMessage       = "KamsoraAPM Collector: log ingestion buffer full or mapping failed.",
            };
        }
        return Task.FromResult(response);
    }
}

/// <summary>OTLP <c>MetricsService.Export</c>.</summary>
public sealed class OtlpMetricsService : MetricsService.MetricsServiceBase
{
    private readonly ChannelWriter<MetricPointRow> _writer;
    private readonly MetricCardinalityGuard _cardinality;
    private readonly ILogger<OtlpMetricsService> _logger;
    private long _droppedRows;

    public OtlpMetricsService(
        ChannelWriter<MetricPointRow> writer,
        MetricCardinalityGuard cardinality,
        ILogger<OtlpMetricsService> logger)
    {
        _writer      = writer;
        _cardinality = cardinality;
        _logger      = logger;
    }

    public long DroppedRows => Interlocked.Read(ref _droppedRows);

    public override Task<ExportMetricsServiceResponse> Export(ExportMetricsServiceRequest request, ServerCallContext context)
    {
        var tenant = TenantContextAccessor.GetTenant(context);
        long rejected = 0;

        foreach (var resourceMetrics in request.ResourceMetrics)
        {
            foreach (var scope in resourceMetrics.ScopeMetrics)
            {
                foreach (var metric in scope.Metrics)
                {
                    if (!_cardinality.TryAdmit(tenant.TenantId, metric.Name))
                    {
                        rejected++;
                        continue;
                    }

                    IReadOnlyList<MetricPointRow> rows;
                    try
                    {
                        rows = MetricRowMapper.ToRows(tenant.TenantId, resourceMetrics, metric).ToList();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "KamsoraAPM Collector: failed to map OTLP metric {Name} (tenant={Tenant}); rejected.",
                            metric.Name, tenant.TenantId);
                        rejected++;
                        continue;
                    }

                    foreach (var row in rows)
                    {
                        if (!_writer.TryWrite(row))
                        {
                            rejected++;
                            Interlocked.Increment(ref _droppedRows);
                        }
                    }
                }
            }
        }

        var response = new ExportMetricsServiceResponse();
        if (rejected > 0)
        {
            response.PartialSuccess = new ExportMetricsPartialSuccess
            {
                RejectedDataPoints = rejected,
                ErrorMessage       = "KamsoraAPM Collector: metric ingestion buffer full, cardinality cap hit, or mapping failed.",
            };
        }
        return Task.FromResult(response);
    }
}
