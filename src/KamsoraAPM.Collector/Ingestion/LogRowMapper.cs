// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Common.V1;
using KamsoraAPM.Contracts.Logs.V1;
using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// Converts an inbound protobuf <see cref="LogRecord"/> + its enclosing
/// <see cref="ResourceLogs"/> + <see cref="ScopeLogs"/> into a storage-layer
/// <see cref="LogRow"/>.
/// </summary>
internal static class LogRowMapper
{
    private const string AttrServiceName       = "service.name";
    private const string AttrServiceNamespace  = "service.namespace";
    private const string AttrAgentVersion      = "kamsora.agent.version";

    public static LogRow ToRow(Guid tenantId, ResourceLogs resourceLogs, ScopeLogs scope, LogRecord record)
    {
        var (serviceName, serviceNamespace, agentVersion) =
            ExtractResourceAttributes(resourceLogs.Resource);

        var row = new LogRow
        {
            TenantId             = tenantId,
            TimeUnixNano         = TimestampGuard.ClampNanos(
                record.TimeUnixNano == 0 ? record.ObservedTimeUnixNano : record.TimeUnixNano),
            ObservedTimeUnixNano = TimestampGuard.ClampNanos(
                record.ObservedTimeUnixNano == 0 ? record.TimeUnixNano : record.ObservedTimeUnixNano),
            ServiceName          = serviceName,
            ServiceNamespace     = serviceNamespace,
            SeverityNumber       = (byte)Math.Clamp((int)record.SeverityNumber, 0, 255),
            SeverityText         = string.IsNullOrEmpty(record.SeverityText)
                ? DefaultSeverityText(record.SeverityNumber)
                : record.SeverityText,
            Body                 = AnyValueToString(record.Body),
            TraceId              = record.TraceId.Length == 16 ? record.TraceId.ToByteArray() : new byte[16],
            SpanId               = record.SpanId.Length  == 8  ? record.SpanId.ToByteArray()  : new byte[8],
            AgentVersion         = agentVersion,
        };

        if (record.Attributes.Count > 0)
        {
            var keys   = new string[record.Attributes.Count];
            var values = new string[record.Attributes.Count];
            for (int i = 0; i < record.Attributes.Count; i++)
            {
                keys[i]   = record.Attributes[i].Key;
                values[i] = AnyValueToString(record.Attributes[i].Value);
            }
            row.AttrsKeys   = keys;
            row.AttrsValues = values;
        }

        return row;
    }

    private static (string serviceName, string serviceNamespace, string agentVersion)
        ExtractResourceAttributes(Resource? resource)
    {
        string serviceName = string.Empty, serviceNamespace = string.Empty, agentVersion = string.Empty;
        if (resource is null) return (serviceName, serviceNamespace, agentVersion);

        foreach (var kv in resource.Attributes)
        {
            var v = AnyValueToString(kv.Value);
            switch (kv.Key)
            {
                case AttrServiceName:      serviceName      = v; break;
                case AttrServiceNamespace: serviceNamespace = v; break;
                case AttrAgentVersion:     agentVersion     = v; break;
            }
        }
        return (serviceName, serviceNamespace, agentVersion);
    }

    private static string DefaultSeverityText(LogRecord.Types.SeverityNumber sev) => sev switch
    {
        LogRecord.Types.SeverityNumber.Trace => "TRACE",
        LogRecord.Types.SeverityNumber.Debug => "DEBUG",
        LogRecord.Types.SeverityNumber.Info  => "INFO",
        LogRecord.Types.SeverityNumber.Warn  => "WARN",
        LogRecord.Types.SeverityNumber.Error => "ERROR",
        LogRecord.Types.SeverityNumber.Fatal => "FATAL",
        _                                    => string.Empty,
    };

    private static string AnyValueToString(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.IntValue    => value.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.BoolValue   => value.BoolValue ? "true" : "false",
        AnyValue.ValueOneofCase.BytesValue  => Convert.ToBase64String(value.BytesValue.Span),
        _                                   => string.Empty,
    };
}
