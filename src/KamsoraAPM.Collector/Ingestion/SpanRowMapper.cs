// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Common.V1;
using KamsoraAPM.Contracts.Trace.V1;
using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// Converts an inbound protobuf <see cref="Span"/> + its enclosing
/// <see cref="ResourceSpans"/> into a storage-layer <see cref="SpanRow"/>.
///
/// Hoisted attributes (http.method, http.route, http.response.status_code,
/// db.system, db.statement) are extracted from <c>span.Attributes</c> so
/// dashboard queries can filter without unpacking the attrs_keys array.
/// </summary>
internal static class SpanRowMapper
{
    private const string AttrServiceName       = "service.name";
    private const string AttrServiceNamespace  = "service.namespace";
    private const string AttrServiceVersion    = "service.version";
    private const string AttrAgentVersion      = "kamsora.agent.version";

    private const string AttrHttpMethod        = "http.request.method";
    private const string AttrHttpRoute         = "http.route";
    private const string AttrHttpStatusCode    = "http.response.status_code";
    private const string AttrUrlFull           = "url.full";
    private const string AttrClientAddress     = "client.address";
    private const string AttrDbSystem          = "db.system";
    private const string AttrDbStatement       = "db.statement";
    private const string AttrKamsoraConsumerId = "kamsora.consumer.id";

    public static SpanRow ToRow(Guid tenantId, ResourceSpans resourceSpans, ScopeSpans scope, Span span)
    {
        var (serviceName, serviceNamespace, serviceVersion, agentVersion) =
            ExtractResourceAttributes(resourceSpans.Resource);

        var (startNs, endNs) = TimestampGuard.ClampSpan(span.StartTimeUnixNano, span.EndTimeUnixNano);

        var row = new SpanRow
        {
            TenantId          = tenantId,
            TraceId           = span.TraceId.ToByteArray(),
            SpanId            = span.SpanId.ToByteArray(),
            ParentSpanId      = span.ParentSpanId.ToByteArray(),
            TraceState        = span.TraceState,
            SpanName          = span.Name,
            SpanKind          = span.Kind switch
            {
                Span.Types.SpanKind.Server   => "SERVER",
                Span.Types.SpanKind.Client   => "CLIENT",
                Span.Types.SpanKind.Producer => "PRODUCER",
                Span.Types.SpanKind.Consumer => "CONSUMER",
                _                            => "INTERNAL",
            },
            StartTimeUnixNano = startNs,
            EndTimeUnixNano   = endNs,
            StatusCode        = span.Status?.Code switch
            {
                Status.Types.StatusCode.Ok    => "OK",
                Status.Types.StatusCode.Error => "ERROR",
                _                             => "UNSET",
            },
            StatusMessage     = span.Status?.Message ?? string.Empty,
            ServiceName       = serviceName,
            ServiceNamespace  = serviceNamespace,
            ServiceVersion    = serviceVersion,
            AgentVersion      = agentVersion,
        };

        var residualKeys   = new List<string>(span.Attributes.Count);
        var residualValues = new List<string>(span.Attributes.Count);

        foreach (var kv in span.Attributes)
        {
            switch (kv.Key)
            {
                case AttrHttpMethod:
                    row.HttpMethod = AnyValueToString(kv.Value);
                    break;
                case AttrHttpRoute:
                    row.HttpRoute = AnyValueToString(kv.Value);
                    break;
                case AttrHttpStatusCode:
                    if (TryAnyValueToInt(kv.Value, out var statusCode))
                        row.HttpStatusCode = (ushort)Math.Clamp(statusCode, 0, ushort.MaxValue);
                    break;
                case AttrUrlFull:
                    row.HttpUrl = AnyValueToString(kv.Value);
                    break;
                case AttrClientAddress:
                    row.HttpClientIp = AnyValueToString(kv.Value);
                    break;
                case AttrDbSystem:
                    row.DbSystem = AnyValueToString(kv.Value);
                    break;
                case AttrDbStatement:
                    row.DbStatement = AnyValueToString(kv.Value);
                    break;
                case AttrKamsoraConsumerId:
                    row.ConsumerId = AnyValueToString(kv.Value);
                    break;
                default:
                    residualKeys.Add(kv.Key);
                    residualValues.Add(AnyValueToString(kv.Value));
                    break;
            }
        }

        row.AttrsKeys   = residualKeys.ToArray();
        row.AttrsValues = residualValues.ToArray();

        if (span.Events.Count > 0)
        {
            row.EventNames       = new string[span.Events.Count];
            row.EventTimesUnixNs = new ulong[span.Events.Count];
            row.EventAttrsJson   = new string[span.Events.Count];
            for (int i = 0; i < span.Events.Count; i++)
            {
                var ev = span.Events[i];
                row.EventNames[i]       = ev.Name;
                row.EventTimesUnixNs[i] = ev.TimeUnixNano;
                row.EventAttrsJson[i]   = EventAttrsToJson(ev.Attributes);
            }
        }

        return row;
    }

    private static (string serviceName, string serviceNamespace, string serviceVersion, string agentVersion)
        ExtractResourceAttributes(Resource? resource)
    {
        string serviceName = string.Empty, serviceNamespace = string.Empty;
        string serviceVersion = string.Empty, agentVersion = string.Empty;
        if (resource is null) return (serviceName, serviceNamespace, serviceVersion, agentVersion);

        foreach (var kv in resource.Attributes)
        {
            var value = AnyValueToString(kv.Value);
            switch (kv.Key)
            {
                case AttrServiceName:      serviceName      = value; break;
                case AttrServiceNamespace: serviceNamespace = value; break;
                case AttrServiceVersion:   serviceVersion   = value; break;
                case AttrAgentVersion:     agentVersion     = value; break;
            }
        }
        return (serviceName, serviceNamespace, serviceVersion, agentVersion);
    }

    private static string AnyValueToString(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.IntValue    => value.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.BoolValue   => value.BoolValue ? "true" : "false",
        AnyValue.ValueOneofCase.BytesValue  => Convert.ToBase64String(value.BytesValue.Span),
        _                                   => string.Empty,
    };

    private static bool TryAnyValueToInt(AnyValue? value, out long result)
    {
        switch (value?.ValueCase)
        {
            case AnyValue.ValueOneofCase.IntValue:
                result = value.IntValue;
                return true;
            case AnyValue.ValueOneofCase.DoubleValue:
                result = (long)value.DoubleValue;
                return true;
            case AnyValue.ValueOneofCase.StringValue:
                return long.TryParse(value.StringValue, System.Globalization.NumberStyles.Integer,
                                     System.Globalization.CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }

    private static string EventAttrsToJson(IEnumerable<KeyValue> attrs)
    {
        // Tight string allocation - small payloads, called per event. Replaced
        // with `Utf8JsonWriter` if it ever shows up in profiles.
        var sb = new System.Text.StringBuilder("{");
        bool first = true;
        foreach (var kv in attrs)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(System.Text.Json.JsonEncodedText.Encode(kv.Key)).Append("\":\"")
              .Append(System.Text.Json.JsonEncodedText.Encode(AnyValueToString(kv.Value))).Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }
}
