// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Models;

/// <summary>
/// Storage-shaped representation of one CPU profile capture, ready for
/// bulk-insert into <c>kamsora_apm.profiles</c>.
/// </summary>
public sealed class ProfileRow
{
    public Guid    TenantId          { get; set; }
    /// <summary>Wall-clock start of the capture window. Nanoseconds since unix epoch.</summary>
    public ulong   StartTimeUnixNano { get; set; }
    /// <summary>Duration of the capture window in nanoseconds.</summary>
    public ulong   DurationNanos     { get; set; }

    public string  ServiceName       { get; set; } = string.Empty;
    public string  ServiceNamespace  { get; set; } = string.Empty;

    /// <summary>One of <c>CPU</c>, <c>WALL</c>, <c>ALLOC</c>, <c>LOCK</c>, <c>GC</c>.</summary>
    public string  ProfileKind       { get; set; } = "CPU";
    public ulong   SampleCount       { get; set; }

    /// <summary>Raw perftools.profiles.Profile bytes; encoded to Base64 by the writer.</summary>
    public byte[]  PprofBytes        { get; set; } = Array.Empty<byte>();

    /// <summary>Lowercase 32-char hex trace id when the capture was triggered for a single request. Empty for periodic captures.</summary>
    public string  TriggerTraceIdHex { get; set; } = string.Empty;

    public string[] AttrsKeys        { get; set; } = Array.Empty<string>();
    public string[] AttrsValues      { get; set; } = Array.Empty<string>();

    public string  AgentVersion      { get; set; } = string.Empty;
}
