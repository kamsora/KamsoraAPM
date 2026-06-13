// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Channels;
using Grpc.Core;
using KamsoraAPM.Contracts.Collector.V1;
using KamsoraAPM.Storage.Models;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// gRPC <c>LogsService.Export</c> handler. Each inbound <c>LogRecord</c> is
/// mapped to a <see cref="LogRow"/> and pushed onto the bounded channel
/// consumed by <c>BatchFlusherHostedService&lt;LogRow&gt;</c>.
/// </summary>
public sealed class KamsoraLogsService : LogsService.LogsServiceBase
{
    private readonly ChannelWriter<LogRow> _writer;
    private readonly ILogger<KamsoraLogsService> _logger;
    private long _droppedRows;

    public KamsoraLogsService(ChannelWriter<LogRow> writer, ILogger<KamsoraLogsService> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    public long DroppedRows => Interlocked.Read(ref _droppedRows);

    public override Task<ExportLogsResponse> Export(ExportLogsRequest request, ServerCallContext context)
    {
        var tenant = TenantContextAccessor.GetTenant(context);

        int accepted = 0;
        int rejected = 0;

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
                            "KamsoraAPM Collector: failed to map inbound log (tenant={Tenant}). Record rejected.",
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

        var response = new ExportLogsResponse();
        if (rejected > 0)
        {
            response.PartialSuccess = new ExportPartialSuccess
            {
                RejectedItems = rejected,
                ErrorMessage  = "KamsoraAPM Collector: log ingestion buffer full or mapping failed.",
            };
            _logger.LogWarning("KamsoraAPM Collector: logs tenant={Tenant} accepted={Accepted} rejected={Rejected}.",
                tenant.TenantId, accepted, rejected);
        }

        return Task.FromResult(response);
    }
}
