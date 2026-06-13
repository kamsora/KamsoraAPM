// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Models;

/// <summary>
/// Storage-shaped representation of one OTel log record, ready for bulk-insert
/// into <c>kamsora_apm.logs</c>.
/// </summary>
public sealed class LogRow
{
    public Guid    TenantId             { get; set; }
    /// <summary>Event timestamp (when the log was created). Nanos since unix epoch.</summary>
    public ulong   TimeUnixNano         { get; set; }
    /// <summary>When the Collector saw it; same as <see cref="TimeUnixNano"/> if upstream didn't set it.</summary>
    public ulong   ObservedTimeUnixNano { get; set; }

    public string  ServiceName       { get; set; } = string.Empty;
    public string  ServiceNamespace  { get; set; } = string.Empty;

    /// <summary>OTLP numeric severity (1..24). 0 = unspecified.</summary>
    public byte    SeverityNumber    { get; set; }
    /// <summary>Free-text severity label (TRACE/DEBUG/INFO/WARN/ERROR/FATAL) - keeps display logic dumb.</summary>
    public string  SeverityText      { get; set; } = string.Empty;
    public string  Body              { get; set; } = string.Empty;

    /// <summary>16 raw bytes when correlated with a trace, otherwise zero-padded.</summary>
    public byte[]  TraceId           { get; set; } = new byte[16];
    /// <summary>8 raw bytes; zero-padded when not correlated to a span.</summary>
    public byte[]  SpanId            { get; set; } = new byte[8];

    public string[] AttrsKeys        { get; set; } = Array.Empty<string>();
    public string[] AttrsValues      { get; set; } = Array.Empty<string>();

    public string  AgentVersion      { get; set; } = string.Empty;
}
