// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Google.Protobuf;
using KamsoraAPM.Contracts.Common.V1;
using KamsoraAPM.Contracts.Trace.V1;

namespace KamsoraAPM.Agent.Internal;

/// <summary>
/// Converts a completed <see cref="Activity"/> into a Kamsora <see cref="Span"/>.
/// Allocation-conscious: reuses pooled buffers for the 16-byte trace id and the
/// 8-byte span id; everything else is unavoidable protobuf object construction.
/// </summary>
internal static class SpanFactory
{
    private const string AttrHttpRequestMethod    = "http.request.method";
    private const string AttrHttpRoute            = "http.route";
    private const string AttrHttpResponseStatus   = "http.response.status_code";
    private const string AttrUrlFull              = "url.full";
    private const string AttrUserAgentOriginal    = "user_agent.original";
    private const string AttrClientAddress        = "client.address";

    public static Span FromActivity(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        Span<byte> traceIdBuf = stackalloc byte[16];
        Span<byte> spanIdBuf  = stackalloc byte[8];
        activity.TraceId.CopyTo(traceIdBuf);
        activity.SpanId.CopyTo(spanIdBuf);

        var span = new Span
        {
            TraceId           = ByteString.CopyFrom(traceIdBuf),
            SpanId            = ByteString.CopyFrom(spanIdBuf),
            Name              = activity.DisplayName,
            Kind              = MapKind(activity.Kind),
            StartTimeUnixNano = ToUnixNanos(activity.StartTimeUtc),
            EndTimeUnixNano   = ToUnixNanos(activity.StartTimeUtc + activity.Duration),
            TraceState        = activity.TraceStateString ?? string.Empty,
            Status            = MapStatus(activity),
        };

        if (activity.ParentSpanId != default)
        {
            Span<byte> parentBuf = stackalloc byte[8];
            activity.ParentSpanId.CopyTo(parentBuf);
            span.ParentSpanId = ByteString.CopyFrom(parentBuf);
        }

        foreach (var tag in activity.TagObjects)
        {
            if (tag.Value is null) continue;
            span.Attributes.Add(new KeyValue
            {
                Key   = tag.Key,
                Value = ToAnyValue(tag.Value),
            });
        }

        foreach (var ev in activity.Events)
        {
            var protoEvent = new Span.Types.Event
            {
                Name         = ev.Name,
                TimeUnixNano = ToUnixNanos(ev.Timestamp.UtcDateTime),
            };
            foreach (var tag in ev.Tags)
            {
                if (tag.Value is null) continue;
                protoEvent.Attributes.Add(new KeyValue { Key = tag.Key, Value = ToAnyValue(tag.Value) });
            }
            span.Events.Add(protoEvent);
        }

        return span;
    }

    private static Span.Types.SpanKind MapKind(ActivityKind kind) => kind switch
    {
        ActivityKind.Server   => Span.Types.SpanKind.Server,
        ActivityKind.Client   => Span.Types.SpanKind.Client,
        ActivityKind.Producer => Span.Types.SpanKind.Producer,
        ActivityKind.Consumer => Span.Types.SpanKind.Consumer,
        ActivityKind.Internal => Span.Types.SpanKind.Internal,
        _                     => Span.Types.SpanKind.Unspecified,
    };

    private static Status MapStatus(Activity activity)
    {
        var status = new Status();
        switch (activity.Status)
        {
            case ActivityStatusCode.Ok:
                status.Code = Status.Types.StatusCode.Ok;
                break;
            case ActivityStatusCode.Error:
                status.Code    = Status.Types.StatusCode.Error;
                status.Message = activity.StatusDescription ?? string.Empty;
                break;
            default:
                status.Code = Status.Types.StatusCode.Unset;
                break;
        }

        // For HTTP server spans, ASP.NET Core does NOT set ActivityStatus to Error
        // for 5xx responses by default. Apply OTLP convention here.
        if (activity.Kind == ActivityKind.Server &&
            status.Code == Status.Types.StatusCode.Unset &&
            TryGetHttpStatusCode(activity, out var httpStatus) &&
            httpStatus >= 500)
        {
            status.Code = Status.Types.StatusCode.Error;
        }

        return status;
    }

    private static bool TryGetHttpStatusCode(Activity activity, out int httpStatus)
    {
        foreach (var tag in activity.TagObjects)
        {
            if (!string.Equals(tag.Key, AttrHttpResponseStatus, StringComparison.Ordinal)) continue;
            if (tag.Value is int i) { httpStatus = i; return true; }
            if (tag.Value is long l && l is >= 0 and <= int.MaxValue) { httpStatus = (int)l; return true; }
            if (tag.Value is string s && int.TryParse(s, out var parsed)) { httpStatus = parsed; return true; }
        }
        httpStatus = 0;
        return false;
    }

    private static AnyValue ToAnyValue(object value) => value switch
    {
        bool   b => new AnyValue { BoolValue   = b },
        int    i => new AnyValue { IntValue    = i },
        long   l => new AnyValue { IntValue    = l },
        short  s => new AnyValue { IntValue    = s },
        byte   y => new AnyValue { IntValue    = y },
        double d => new AnyValue { DoubleValue = d },
        float  f => new AnyValue { DoubleValue = f },
        _        => new AnyValue { StringValue = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty },
    };

    private static ulong ToUnixNanos(DateTime utc)
    {
        // DateTime ticks are 100 ns. Unix epoch in ticks:
        const long UnixEpochTicks = 621_355_968_000_000_000L;
        long ticks = utc.Kind == DateTimeKind.Utc ? utc.Ticks : utc.ToUniversalTime().Ticks;
        long deltaTicks = ticks - UnixEpochTicks;
        return deltaTicks <= 0 ? 0UL : (ulong)deltaTicks * 100UL;
    }
}
