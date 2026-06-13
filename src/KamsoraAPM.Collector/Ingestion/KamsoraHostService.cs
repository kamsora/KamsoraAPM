// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Channels;
using Grpc.Core;
using KamsoraAPM.Contracts.Collector.V1;
using KamsoraAPM.Storage.Models;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// gRPC <c>HostService.Export</c> handler. Maps each inbound
/// <see cref="Contracts.Host.V1.HostSnapshot"/> into four row families
/// (cpu/memory, disks, networks, processes) and enqueues them onto the
/// per-table bounded channels. Persistence is asynchronous; one
/// <see cref="BatchFlusherHostedService{TRow}"/> per channel.
/// </summary>
public sealed class KamsoraHostService : HostService.HostServiceBase
{
    private readonly ChannelWriter<HostCpuMemoryRow> _cpuMemoryWriter;
    private readonly ChannelWriter<HostDiskRow>      _disksWriter;
    private readonly ChannelWriter<HostNetworkRow>   _networksWriter;
    private readonly ChannelWriter<HostProcessRow>   _processesWriter;
    private readonly ILogger<KamsoraHostService> _logger;
    private long _droppedRows;

    public KamsoraHostService(
        ChannelWriter<HostCpuMemoryRow> cpuMemoryWriter,
        ChannelWriter<HostDiskRow>      disksWriter,
        ChannelWriter<HostNetworkRow>   networksWriter,
        ChannelWriter<HostProcessRow>   processesWriter,
        ILogger<KamsoraHostService> logger)
    {
        _cpuMemoryWriter = cpuMemoryWriter;
        _disksWriter     = disksWriter;
        _networksWriter  = networksWriter;
        _processesWriter = processesWriter;
        _logger          = logger;
    }

    public long DroppedRows => Interlocked.Read(ref _droppedRows);

    public override Task<ExportHostResponse> Export(ExportHostRequest request, ServerCallContext context)
    {
        var tenant = TenantContextAccessor.GetTenant(context);

        int accepted = 0;
        int rejected = 0;

        foreach (var snapshot in request.Snapshots)
        {
            try
            {
                var cpuRow      = HostRowMapper.ToCpuMemoryRow(tenant.TenantId, snapshot);
                var diskRows    = HostRowMapper.ToDiskRows   (tenant.TenantId, snapshot);
                var networkRows = HostRowMapper.ToNetworkRows(tenant.TenantId, snapshot);
                var procRows    = HostRowMapper.ToProcessRows(tenant.TenantId, snapshot);

                if (_cpuMemoryWriter.TryWrite(cpuRow)) accepted++; else { rejected++; Interlocked.Increment(ref _droppedRows); }
                foreach (var r in diskRows)    if (!_disksWriter.TryWrite(r))     { rejected++; Interlocked.Increment(ref _droppedRows); }
                foreach (var r in networkRows) if (!_networksWriter.TryWrite(r))  { rejected++; Interlocked.Increment(ref _droppedRows); }
                foreach (var r in procRows)    if (!_processesWriter.TryWrite(r)) { rejected++; Interlocked.Increment(ref _droppedRows); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "KamsoraAPM Collector: failed to map host snapshot (tenant={Tenant}). Snapshot rejected.",
                    tenant.TenantId);
                rejected++;
            }
        }

        if (rejected > 0)
        {
            _logger.LogWarning(
                "KamsoraAPM Collector: host ingest tenant={Tenant} accepted={Accepted} rejected={Rejected}.",
                tenant.TenantId, accepted, rejected);
        }

        return Task.FromResult(new ExportHostResponse());
    }
}
