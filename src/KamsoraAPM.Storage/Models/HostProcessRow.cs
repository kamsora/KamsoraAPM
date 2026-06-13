// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Models;

/// <summary>Storage-shaped row for <c>kamsora_apm.host_processes</c>. One row per process per snapshot.</summary>
public sealed class HostProcessRow
{
    public Guid   TenantId        { get; set; }
    public ulong  TimeUnixNano    { get; set; }
    public string HostId          { get; set; } = string.Empty;
    public uint   Pid             { get; set; }
    public string Command         { get; set; } = string.Empty;
    public string UserName        { get; set; } = string.Empty;
    public string RuntimeVersion  { get; set; } = string.Empty;
    public string ServiceName     { get; set; } = string.Empty;
    public float  CpuUtilization  { get; set; }
    public ulong  RssBytes        { get; set; }
    public uint   ThreadCount     { get; set; }
    public uint   HandleCount     { get; set; }
}
