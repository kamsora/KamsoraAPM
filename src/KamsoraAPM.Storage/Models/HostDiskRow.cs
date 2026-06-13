// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Models;

/// <summary>Storage-shaped row for <c>kamsora_apm.host_disks</c>. One row per volume per snapshot.</summary>
public sealed class HostDiskRow
{
    public Guid   TenantId           { get; set; }
    public ulong  TimeUnixNano       { get; set; }
    public string HostId             { get; set; } = string.Empty;
    public string Device             { get; set; } = string.Empty;
    public string Mountpoint         { get; set; } = string.Empty;
    public ulong  TotalBytes         { get; set; }
    public ulong  UsedBytes          { get; set; }
    public ulong  ReadsPerSec        { get; set; }
    public ulong  WritesPerSec       { get; set; }
    public ulong  ReadBytesPerSec    { get; set; }
    public ulong  WriteBytesPerSec   { get; set; }
}
