// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Channels;
using Grpc.Core;
using KamsoraAPM.Contracts.Collector.V1;
using KamsoraAPM.Storage.Models;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// gRPC <c>TraceService.Export</c> handler. Validates batch shape, maps
/// each protobuf <c>Span</c> to a <see cref="SpanRow"/>, and enqueues it
/// onto the in-process bounded channel. Persistence happens asynchronously
/// in <see cref="SpanFlusherHostedService"/>.
/// </summary>
public sealed class KamsoraTraceService : TraceService.TraceServiceBase
{
    private readonly ChannelWriter<SpanRow> _writer;
    private readonly ILogger<KamsoraTraceService> _logger;
    private long _droppedRows;

    public KamsoraTraceService(ChannelWriter<SpanRow> writer, ILogger<KamsoraTraceService> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    /// <summary>Diagnostic counter: how many rows the Collector dropped because the channel was full.</summary>
    public long DroppedRows => Interlocked.Read(ref _droppedRows);

    public override Task<ExportTraceResponse> Export(ExportTraceRequest request, ServerCallContext context)
    {
        var tenant = TenantContextAccessor.GetTenant(context);

        int accepted = 0;
        int rejected = 0;

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
                            "KamsoraAPM Collector: failed to map inbound span (tenant={Tenant}). Span rejected.",
                            tenant.TenantId);
                        rejected++;
                        continue;
                    }

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

        var response = new ExportTraceResponse();
        if (rejected > 0)
        {
            response.PartialSuccess = new ExportPartialSuccess
            {
                RejectedItems = rejected,
                ErrorMessage  = "KamsoraAPM Collector: ingestion buffer full or span mapping failed.",
            };
            _logger.LogWarning("KamsoraAPM Collector: tenant={Tenant} accepted={Accepted} rejected={Rejected}.",
                tenant.TenantId, accepted, rejected);
        }

        return Task.FromResult(response);
    }
}
