// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using KamsoraAPM.Agent.Options;
using KamsoraAPM.Contracts.Collector.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;

// Proto types are fully-qualified - `LogRecord` in this file always means
// the OTel SDK type, never the proto. This avoids the type-alias collision
// that previously made the compiler resolve `rec.Severity` against the wrong
// type.
using ProtoLogRecord  = KamsoraAPM.Contracts.Logs.V1.LogRecord;
using ProtoResLogs    = KamsoraAPM.Contracts.Logs.V1.ResourceLogs;
using ProtoScopeLogs  = KamsoraAPM.Contracts.Logs.V1.ScopeLogs;
using ProtoSeverity   = KamsoraAPM.Contracts.Logs.V1.LogRecord.Types.SeverityNumber;
using ProtoInstScope  = KamsoraAPM.Contracts.Common.V1.InstrumentationScope;
using ProtoKv         = KamsoraAPM.Contracts.Common.V1.KeyValue;
using ProtoAnyValue   = KamsoraAPM.Contracts.Common.V1.AnyValue;
using ProtoResource   = KamsoraAPM.Contracts.Common.V1.Resource;

namespace KamsoraAPM.Agent.Internal;

/// <summary>
/// OTel <see cref="LogRecord"/> exporter that pushes batches over gRPC to the
/// KamsoraAPM Collector. Plugged into the standard
/// <c>builder.Logging.AddOpenTelemetry()</c> pipeline as a
/// <see cref="BaseExporter{T}"/>.
/// </summary>
internal sealed class KamsoraApmLogExporter : BaseExporter<LogRecord>
{
    private readonly KamsoraApmOptions _options;
    private readonly ILogger<KamsoraApmLogExporter> _logger;
    private readonly GrpcChannel _grpcChannel;
    private readonly LogsService.LogsServiceClient _logsClient;
    private readonly ProtoResource _resource;

    public KamsoraApmLogExporter(IOptions<KamsoraApmOptions> options, ILogger<KamsoraApmLogExporter> logger)
    {
        _options = options.Value;
        _logger  = logger;

        _grpcChannel = KamsoraGrpcChannelFactory.Create(_options);
        _logsClient = new LogsService.LogsServiceClient(_grpcChannel);
        _resource   = BuildResource(_options);
    }

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        if (batch.Count == 0) return ExportResult.Success;

        var request      = new ExportLogsRequest();
        var resourceLogs = new ProtoResLogs { Resource = _resource };
        var scope        = new ProtoScopeLogs
        {
            Scope = new ProtoInstScope
            {
                Name    = "KamsoraAPM.Agent",
                Version = KamsoraApmAgent.Version,
            },
        };
        resourceLogs.ScopeLogs.Add(scope);
        request.ResourceLogs.Add(resourceLogs);

        foreach (var rec in batch)
        {
            try
            {
                scope.LogRecords.Add(ToProto(rec));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KamsoraAPM Agent: failed to map LogRecord; skipping one log.");
            }
        }

        if (scope.LogRecords.Count == 0) return ExportResult.Success;

        var headers = KamsoraGrpcChannelFactory.BuildAuthMetadata(_options);

        // Keep our own export RPC out of the captured telemetry.
        using var suppressSelfTrace = AgentSelfTrace.Suppress();
        try
        {
            using var call = _logsClient.ExportAsync(
                request,
                new CallOptions(headers: headers, deadline: DateTime.UtcNow.Add(_options.ExportTimeout)));
            var response = call.ResponseAsync.GetAwaiter().GetResult();
            if (response.PartialSuccess is { RejectedItems: > 0 })
            {
                _logger.LogWarning(
                    "KamsoraAPM Agent: log Collector partial-success - {Rejected} rejected: {Msg}",
                    response.PartialSuccess.RejectedItems, response.PartialSuccess.ErrorMessage);
            }
            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM Agent: log export to {Endpoint} failed. Batch size {Count}.",
                _options.Endpoint, scope.LogRecords.Count);
            return ExportResult.Failure;
        }
    }

    private static ProtoLogRecord ToProto(OpenTelemetry.Logs.LogRecord rec)
    {
        // OTel 1.12 SDK doesn't expose Severity/SeverityText/ObservedTimestamp
        // as public properties on LogRecord - they live in the internal
        // LogRecordData struct. We derive severity from the MEL LogLevel,
        // which maps 1:1 to OTLP's tier ranges.
        var severityProto = MapMelLogLevel(rec.LogLevel);
        var proto = new ProtoLogRecord
        {
            TimeUnixNano         = ToUnixNanos(rec.Timestamp),
            ObservedTimeUnixNano = ToUnixNanos(rec.Timestamp),
            SeverityNumber       = severityProto,
            SeverityText         = rec.LogLevel.ToString().ToUpperInvariant(),
            Body                 = new ProtoAnyValue { StringValue = rec.FormattedMessage ?? rec.Body ?? string.Empty },
        };

        if (rec.TraceId != default)
        {
            Span<byte> traceId = stackalloc byte[16];
            rec.TraceId.CopyTo(traceId);
            proto.TraceId = ByteString.CopyFrom(traceId);
        }
        if (rec.SpanId != default)
        {
            Span<byte> spanId = stackalloc byte[8];
            rec.SpanId.CopyTo(spanId);
            proto.SpanId = ByteString.CopyFrom(spanId);
        }

        if (!string.IsNullOrEmpty(rec.CategoryName))
            proto.Attributes.Add(KvOf("category", rec.CategoryName));
        if (rec.EventId.Id != 0 || !string.IsNullOrEmpty(rec.EventId.Name))
            proto.Attributes.Add(KvOf("event.id", rec.EventId.ToString()));
        if (rec.Exception is not null)
        {
            proto.Attributes.Add(KvOf("exception.type",    rec.Exception.GetType().FullName ?? string.Empty));
            proto.Attributes.Add(KvOf("exception.message", rec.Exception.Message            ?? string.Empty));
            proto.Attributes.Add(KvOf("exception.stack",   rec.Exception.StackTrace          ?? string.Empty));
        }
        if (rec.Attributes is not null)
        {
            foreach (var kv in rec.Attributes)
            {
                proto.Attributes.Add(KvOf(kv.Key, kv.Value?.ToString() ?? string.Empty));
            }
        }

        return proto;
    }

    /// <summary>
    /// Map Microsoft.Extensions.Logging severity tiers to OTLP severity buckets.
    /// </summary>
    private static ProtoSeverity MapMelLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace       => ProtoSeverity.Trace,
        LogLevel.Debug       => ProtoSeverity.Debug,
        LogLevel.Information => ProtoSeverity.Info,
        LogLevel.Warning     => ProtoSeverity.Warn,
        LogLevel.Error       => ProtoSeverity.Error,
        LogLevel.Critical    => ProtoSeverity.Fatal,
        _                    => ProtoSeverity.Unspecified,
    };

    private static ulong ToUnixNanos(DateTime utc)
    {
        const long UnixEpochTicks = 621_355_968_000_000_000L;
        if (utc == default) return 0UL;
        long ticks = utc.Kind == DateTimeKind.Utc ? utc.Ticks : utc.ToUniversalTime().Ticks;
        long delta = ticks - UnixEpochTicks;
        return delta <= 0 ? 0UL : (ulong)delta * 100UL;
    }

    private static ProtoResource BuildResource(KamsoraApmOptions options)
    {
        var resource = new ProtoResource();
        resource.Attributes.Add(KvOf("service.name", options.ServiceName));
        if (!string.IsNullOrEmpty(options.ServiceNamespace))
            resource.Attributes.Add(KvOf("service.namespace", options.ServiceNamespace));
        resource.Attributes.Add(KvOf("service.version",
            options.ServiceVersion ?? typeof(KamsoraApmLogExporter).Assembly.GetName().Version?.ToString() ?? "0.0.0"));
        resource.Attributes.Add(KvOf("kamsora.agent.version", KamsoraApmAgent.Version));
        resource.Attributes.Add(KvOf("host.name", Environment.MachineName));
        foreach (var (k, v) in options.ResourceAttributes)
            resource.Attributes.Add(KvOf(k, v));
        return resource;
    }

    private static ProtoKv KvOf(string key, string value) =>
        new() { Key = key, Value = new ProtoAnyValue { StringValue = value ?? string.Empty } };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _grpcChannel.ShutdownAsync().GetAwaiter().GetResult(); } catch { }
            _grpcChannel.Dispose();
        }
        base.Dispose(disposing);
    }
}
