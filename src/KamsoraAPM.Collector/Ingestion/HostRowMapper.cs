// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Common.V1;
using KamsoraAPM.Contracts.Host.V1;
using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// Converts an inbound <see cref="HostSnapshot"/> into a storage-shaped
/// <see cref="HostCpuMemoryRow"/>. Resource attributes (host.id, host.name,
/// os.type, os.version) are pulled from <c>snapshot.Resource.Attributes</c>.
/// </summary>
internal static class HostRowMapper
{
    private const string AttrHostId    = "host.id";
    private const string AttrHostName  = "host.name";
    private const string AttrOsType    = "os.type";
    private const string AttrOsVersion = "os.version";

    public static string ExtractHostId(HostSnapshot snapshot) =>
        ExtractResourceAttributes(snapshot.Resource).hostId;

    public static List<HostDiskRow> ToDiskRows(Guid tenantId, HostSnapshot snapshot)
    {
        if (snapshot.Disks.Count == 0) return new List<HostDiskRow>(0);
        var hostId = ExtractHostId(snapshot);
        var rows   = new List<HostDiskRow>(snapshot.Disks.Count);
        foreach (var d in snapshot.Disks)
        {
            rows.Add(new HostDiskRow
            {
                TenantId         = tenantId,
                TimeUnixNano     = snapshot.TimeUnixNano,
                HostId           = hostId,
                Device           = d.Device ?? string.Empty,
                Mountpoint       = d.Mountpoint ?? string.Empty,
                TotalBytes       = d.TotalBytes,
                UsedBytes        = d.UsedBytes,
                ReadsPerSec      = d.ReadsPerSec,
                WritesPerSec     = d.WritesPerSec,
                ReadBytesPerSec  = d.ReadBytesPerSec,
                WriteBytesPerSec = d.WriteBytesPerSec,
            });
        }
        return rows;
    }

    public static List<HostNetworkRow> ToNetworkRows(Guid tenantId, HostSnapshot snapshot)
    {
        if (snapshot.Networks.Count == 0) return new List<HostNetworkRow>(0);
        var hostId = ExtractHostId(snapshot);
        var rows   = new List<HostNetworkRow>(snapshot.Networks.Count);
        foreach (var n in snapshot.Networks)
        {
            rows.Add(new HostNetworkRow
            {
                TenantId        = tenantId,
                TimeUnixNano    = snapshot.TimeUnixNano,
                HostId          = hostId,
                InterfaceName   = n.InterfaceName ?? string.Empty,
                RxBytesPerSec   = n.RxBytesPerSec,
                TxBytesPerSec   = n.TxBytesPerSec,
                RxPacketsPerSec = n.RxPacketsPerSec,
                TxPacketsPerSec = n.TxPacketsPerSec,
                RxErrors        = n.RxErrors,
                TxErrors        = n.TxErrors,
            });
        }
        return rows;
    }

    public static List<HostProcessRow> ToProcessRows(Guid tenantId, HostSnapshot snapshot)
    {
        if (snapshot.Processes.Count == 0) return new List<HostProcessRow>(0);
        var hostId = ExtractHostId(snapshot);
        var rows   = new List<HostProcessRow>(snapshot.Processes.Count);
        foreach (var p in snapshot.Processes)
        {
            rows.Add(new HostProcessRow
            {
                TenantId       = tenantId,
                TimeUnixNano   = snapshot.TimeUnixNano,
                HostId         = hostId,
                Pid            = p.Pid,
                Command        = p.Command ?? string.Empty,
                UserName       = p.User ?? string.Empty,
                RuntimeVersion = p.RuntimeVersion ?? string.Empty,
                ServiceName    = p.ServiceName ?? string.Empty,
                CpuUtilization = (float)p.CpuUtilization,
                RssBytes       = p.RssBytes,
                ThreadCount    = p.ThreadCount,
                HandleCount    = p.HandleCount,
            });
        }
        return rows;
    }

    public static HostCpuMemoryRow ToCpuMemoryRow(Guid tenantId, HostSnapshot snapshot)
    {
        var (hostId, hostName, osType, osVersion) = ExtractResourceAttributes(snapshot.Resource);

        var cpu = snapshot.Cpu;
        var mem = snapshot.Memory;

        return new HostCpuMemoryRow
        {
            TenantId          = tenantId,
            TimeUnixNano      = snapshot.TimeUnixNano,
            HostId            = hostId,
            HostName          = hostName,
            OsType            = osType,
            OsVersion         = osVersion,
            LogicalCores      = cpu is null ? (ushort)0 : (ushort)Math.Clamp((int)cpu.LogicalCores, 0, ushort.MaxValue),
            Load1m            = (float)(cpu?.Load1M  ?? 0d),
            Load5m            = (float)(cpu?.Load5M  ?? 0d),
            Load15m           = (float)(cpu?.Load15M ?? 0d),
            CpuUtilUser       = (float)(cpu?.UtilizationUser   ?? 0d),
            CpuUtilSystem     = (float)(cpu?.UtilizationSystem ?? 0d),
            CpuUtilIowait     = (float)(cpu?.UtilizationIowait ?? 0d),
            CpuUtilIdle       = (float)(cpu?.UtilizationIdle   ?? 0d),
            MemTotalBytes     = mem?.TotalBytes     ?? 0UL,
            MemAvailableBytes = mem?.AvailableBytes ?? 0UL,
            MemUsedBytes      = mem?.UsedBytes      ?? 0UL,
            SwapTotalBytes    = mem?.SwapTotalBytes ?? 0UL,
            SwapUsedBytes     = mem?.SwapUsedBytes  ?? 0UL,
        };
    }

    private static (string hostId, string hostName, string osType, string osVersion)
        ExtractResourceAttributes(Resource? resource)
    {
        string hostId = string.Empty, hostName = string.Empty;
        string osType = string.Empty, osVersion = string.Empty;
        if (resource is null) return (hostId, hostName, osType, osVersion);

        foreach (var kv in resource.Attributes)
        {
            var v = AnyValueToString(kv.Value);
            switch (kv.Key)
            {
                case AttrHostId:    hostId    = v; break;
                case AttrHostName:  hostName  = v; break;
                case AttrOsType:    osType    = v; break;
                case AttrOsVersion: osVersion = v; break;
            }
        }
        return (hostId, hostName, osType, osVersion);
    }

    private static string AnyValueToString(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.IntValue    => value.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.BoolValue   => value.BoolValue ? "true" : "false",
        _                                   => string.Empty,
    };
}
