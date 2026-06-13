// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Storage.Models;

/// <summary>
/// Storage-shaped representation of a span, ready to be bulk-inserted into
/// the <c>kamsora_apm.spans</c> ClickHouse table. The Collector populates this
/// from the inbound <see cref="KamsoraAPM.Contracts.Trace.V1.Span"/> + resource
/// attributes; the writer is intentionally unaware of the protobuf surface.
/// </summary>
public sealed class SpanRow
{
    public Guid     TenantId          { get; set; }
    /// <summary>Timestamp in nanoseconds since unix epoch.</summary>
    public ulong    StartTimeUnixNano { get; set; }
    public ulong    EndTimeUnixNano   { get; set; }

    public byte[]   TraceId           { get; set; } = Array.Empty<byte>();   // 16 bytes
    public byte[]   SpanId            { get; set; } = Array.Empty<byte>();   // 8 bytes
    public byte[]   ParentSpanId      { get; set; } = Array.Empty<byte>();   // 0 or 8 bytes
    public string   TraceState        { get; set; } = string.Empty;

    public string   ServiceName       { get; set; } = string.Empty;
    public string   ServiceNamespace  { get; set; } = string.Empty;
    public string   ServiceVersion    { get; set; } = string.Empty;
    public string   SpanName          { get; set; } = string.Empty;
    /// <summary>ClickHouse Enum8 value: INTERNAL=1, SERVER=2, CLIENT=3, PRODUCER=4, CONSUMER=5.</summary>
    public string   SpanKind          { get; set; } = "INTERNAL";

    /// <summary>Enum8: UNSET=0, OK=1, ERROR=2.</summary>
    public string   StatusCode        { get; set; } = "UNSET";
    public string   StatusMessage     { get; set; } = string.Empty;

    public string   HttpMethod        { get; set; } = string.Empty;
    public ushort   HttpStatusCode    { get; set; }
    public string   HttpRoute         { get; set; } = string.Empty;
    public string   HttpUrl           { get; set; } = string.Empty;
    public string   HttpClientIp      { get; set; } = string.Empty;

    /// <summary>
    /// Per-request consumer identity for the per-API-key consumer analytics
    /// rollup (M6). Set by the Agent's <c>IConsumerExtractor</c> from a JWT
    /// claim, a request header, or the client IP. Empty when extraction is
    /// disabled or the request had no identifiable consumer.
    /// </summary>
    public string   ConsumerId        { get; set; } = string.Empty;

    public string   DbSystem          { get; set; } = string.Empty;
    public string   DbStatement       { get; set; } = string.Empty;
    public ulong    DbDurationNs      { get; set; }

    public string[] AttrsKeys         { get; set; } = Array.Empty<string>();
    public string[] AttrsValues       { get; set; } = Array.Empty<string>();

    public string[] EventNames        { get; set; } = Array.Empty<string>();
    public ulong[]  EventTimesUnixNs  { get; set; } = Array.Empty<ulong>();
    public string[] EventAttrsJson    { get; set; } = Array.Empty<string>();

    public string   AgentVersion      { get; set; } = string.Empty;
}
