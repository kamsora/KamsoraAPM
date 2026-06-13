// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Models;

/// <summary>Storage-shaped row for <c>kamsora_apm.host_networks</c>. One row per NIC per snapshot.</summary>
public sealed class HostNetworkRow
{
    public Guid   TenantId          { get; set; }
    public ulong  TimeUnixNano      { get; set; }
    public string HostId            { get; set; } = string.Empty;
    public string InterfaceName     { get; set; } = string.Empty;
    public ulong  RxBytesPerSec     { get; set; }
    public ulong  TxBytesPerSec     { get; set; }
    public ulong  RxPacketsPerSec   { get; set; }
    public ulong  TxPacketsPerSec   { get; set; }
    public ulong  RxErrors          { get; set; }
    public ulong  TxErrors          { get; set; }
}
