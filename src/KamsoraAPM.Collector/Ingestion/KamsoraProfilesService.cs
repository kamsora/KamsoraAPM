// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Channels;
using Grpc.Core;
using KamsoraAPM.Contracts.Collector.V1;
using KamsoraAPM.Storage.Models;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// gRPC <c>ProfilesService.Export</c> handler. Each inbound <c>Profile</c> is
/// mapped to a <see cref="ProfileRow"/> and pushed onto the bounded channel
/// consumed by <c>BatchFlusherHostedService&lt;ProfileRow&gt;</c>.
/// </summary>
public sealed class KamsoraProfilesService : ProfilesService.ProfilesServiceBase
{
    private readonly ChannelWriter<ProfileRow> _writer;
    private readonly ILogger<KamsoraProfilesService> _logger;
    private long _droppedRows;

    public KamsoraProfilesService(ChannelWriter<ProfileRow> writer, ILogger<KamsoraProfilesService> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    public long DroppedRows => Interlocked.Read(ref _droppedRows);

    public override Task<ExportProfilesResponse> Export(ExportProfilesRequest request, ServerCallContext context)
    {
        var tenant = TenantContextAccessor.GetTenant(context);

        int accepted = 0;
        int rejected = 0;

        foreach (var resourceProfiles in request.ResourceProfiles)
        {
            foreach (var scope in resourceProfiles.ScopeProfiles)
            {
                foreach (var profile in scope.Profiles)
                {
                    ProfileRow row;
                    try
                    {
                        row = ProfileRowMapper.ToRow(tenant.TenantId, resourceProfiles, scope, profile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "KamsoraAPM Collector: failed to map inbound profile (tenant={Tenant}). Profile rejected.",
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

        var response = new ExportProfilesResponse();
        if (rejected > 0)
        {
            response.PartialSuccess = new ExportPartialSuccess
            {
                RejectedItems = rejected,
                ErrorMessage  = "KamsoraAPM Collector: profile ingestion buffer full or mapping failed.",
            };
            _logger.LogWarning(
                "KamsoraAPM Collector: profiles tenant={Tenant} accepted={Accepted} rejected={Rejected}.",
                tenant.TenantId, accepted, rejected);
        }

        return Task.FromResult(response);
    }
}
