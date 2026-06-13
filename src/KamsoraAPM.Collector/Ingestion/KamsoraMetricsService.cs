// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Channels;
using Grpc.Core;
using KamsoraAPM.Contracts.Collector.V1;
using KamsoraAPM.Storage.Models;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// gRPC <c>MetricsService.Export</c> handler. One inbound <c>Metric</c> can
/// fan-out into N <see cref="MetricPointRow"/> rows (one per data point).
/// </summary>
public sealed class KamsoraMetricsService : MetricsService.MetricsServiceBase
{
    private readonly ChannelWriter<MetricPointRow> _writer;
    private readonly MetricCardinalityGuard _cardinality;
    private readonly ILogger<KamsoraMetricsService> _logger;
    private long _droppedRows;

    public KamsoraMetricsService(
        ChannelWriter<MetricPointRow> writer,
        MetricCardinalityGuard cardinality,
        ILogger<KamsoraMetricsService> logger)
    {
        _writer      = writer;
        _cardinality = cardinality;
        _logger      = logger;
    }

    public long DroppedRows => Interlocked.Read(ref _droppedRows);

    public override Task<ExportMetricsResponse> Export(ExportMetricsRequest request, ServerCallContext context)
    {
        var tenant = TenantContextAccessor.GetTenant(context);

        int accepted = 0;
        int rejected = 0;

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

                    foreach (var row in MapSafe(tenant.TenantId, resourceMetrics, metric))
                    {
                        if (_writer.TryWrite(row))
                        {
                            accepted++;
                        }
                        else
                        {
                            rejected++;
                            Interlocked.Increment(ref _droppedRows);
                        }
                    }
                }
            }
        }

        var response = new ExportMetricsResponse();
        if (rejected > 0)
        {
            response.PartialSuccess = new ExportPartialSuccess
            {
                RejectedItems = rejected,
                ErrorMessage  = "KamsoraAPM Collector: metric ingestion buffer full.",
            };
            _logger.LogWarning("KamsoraAPM Collector: metrics tenant={Tenant} accepted={Accepted} rejected={Rejected}.",
                tenant.TenantId, accepted, rejected);
        }

        return Task.FromResult(response);
    }

    private IEnumerable<MetricPointRow> MapSafe(Guid tenantId, KamsoraAPM.Contracts.Metrics.V1.ResourceMetrics resourceMetrics, KamsoraAPM.Contracts.Metrics.V1.Metric metric)
    {
        IEnumerable<MetricPointRow>? rows = null;
        try
        {
            rows = MetricRowMapper.ToRows(tenantId, resourceMetrics, metric).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "KamsoraAPM Collector: failed to map metric {Name} (tenant={Tenant}); skipping.",
                metric.Name, tenantId);
        }
        return rows ?? Enumerable.Empty<MetricPointRow>();
    }
}
