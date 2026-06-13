// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using Grpc.Core;
using Grpc.Net.Client;
using KamsoraAPM.Agent.Options;
using KamsoraAPM.Contracts.Collector.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;

using PMetric = KamsoraAPM.Contracts.Metrics.V1;
using PCommon = KamsoraAPM.Contracts.Common.V1;

namespace KamsoraAPM.Agent.Internal;

/// <summary>
/// OTel <see cref="Metric"/> exporter. Each push from
/// <see cref="PeriodicExportingMetricReader"/> arrives as a batch of
/// <see cref="Metric"/>; we flatten each metric's data points into the
/// <see cref="PMetric.Metric"/> proto and ship over gRPC.
/// </summary>
internal sealed class KamsoraApmMetricExporter : BaseExporter<Metric>
{
    private readonly KamsoraApmOptions _options;
    private readonly ILogger<KamsoraApmMetricExporter> _logger;
    private readonly GrpcChannel _grpcChannel;
    private readonly MetricsService.MetricsServiceClient _metricsClient;
    private readonly PCommon.Resource _resource;

    public KamsoraApmMetricExporter(IOptions<KamsoraApmOptions> options, ILogger<KamsoraApmMetricExporter> logger)
    {
        _options = options.Value;
        _logger  = logger;

        _grpcChannel   = KamsoraGrpcChannelFactory.Create(_options);
        _metricsClient = new MetricsService.MetricsServiceClient(_grpcChannel);
        _resource      = BuildResource(_options);
    }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        if (batch.Count == 0) return ExportResult.Success;

        var request         = new ExportMetricsRequest();
        var resourceMetrics = new PMetric.ResourceMetrics { Resource = _resource };
        var scope           = new PMetric.ScopeMetrics
        {
            Scope = new PCommon.InstrumentationScope
            {
                Name    = "KamsoraAPM.Agent",
                Version = KamsoraApmAgent.Version,
            },
        };
        resourceMetrics.ScopeMetrics.Add(scope);
        request.ResourceMetrics.Add(resourceMetrics);

        foreach (var otelMetric in batch)
        {
            try
            {
                var protoMetric = ToProto(otelMetric);
                if (protoMetric is not null) scope.Metrics.Add(protoMetric);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KamsoraAPM Agent: failed to map metric {Name}; skipping.", otelMetric.Name);
            }
        }

        if (scope.Metrics.Count == 0) return ExportResult.Success;

        var headers = KamsoraGrpcChannelFactory.BuildAuthMetadata(_options);

        try
        {
            using var call = _metricsClient.ExportAsync(
                request,
                new CallOptions(headers: headers, deadline: DateTime.UtcNow.Add(_options.ExportTimeout)));
            var response = call.ResponseAsync.GetAwaiter().GetResult();
            if (response.PartialSuccess is { RejectedItems: > 0 })
            {
                _logger.LogWarning(
                    "KamsoraAPM Agent: metric Collector partial-success — {Rejected} rejected: {Msg}",
                    response.PartialSuccess.RejectedItems, response.PartialSuccess.ErrorMessage);
            }
            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM Agent: metric export to {Endpoint} failed.", _options.Endpoint);
            return ExportResult.Failure;
        }
    }

    private static PMetric.Metric? ToProto(Metric metric)
    {
        var proto = new PMetric.Metric
        {
            Name        = metric.Name        ?? string.Empty,
            Description = metric.Description ?? string.Empty,
            Unit        = metric.Unit        ?? string.Empty,
        };

        switch (metric.MetricType)
        {
            case MetricType.LongSum or MetricType.LongSumNonMonotonic:
            case MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic:
                proto.Sum = new PMetric.Sum
                {
                    AggregationTemporality = PMetric.AggregationTemporality.Cumulative,
                    IsMonotonic            = metric.MetricType is MetricType.LongSum or MetricType.DoubleSum,
                };
                foreach (ref readonly var p in metric.GetMetricPoints())
                    proto.Sum.DataPoints.Add(NumericPoint(p, metric.MetricType));
                break;

            case MetricType.LongGauge:
            case MetricType.DoubleGauge:
                proto.Gauge = new PMetric.Gauge();
                foreach (ref readonly var p in metric.GetMetricPoints())
                    proto.Gauge.DataPoints.Add(NumericPoint(p, metric.MetricType));
                break;

            case MetricType.Histogram:
                proto.Histogram = new PMetric.Histogram { AggregationTemporality = PMetric.AggregationTemporality.Cumulative };
                foreach (ref readonly var p in metric.GetMetricPoints())
                    proto.Histogram.DataPoints.Add(HistogramPoint(p));
                break;

            default:
                return null;   // exponential histogram / summary — M8.2
        }

        return proto;
    }

    private static PMetric.NumberDataPoint NumericPoint(in MetricPoint p, MetricType kind)
    {
        var dp = new PMetric.NumberDataPoint
        {
            StartTimeUnixNano = ToUnixNanos(p.StartTime.UtcDateTime),
            TimeUnixNano      = ToUnixNanos(p.EndTime.UtcDateTime),
        };
        switch (kind)
        {
            case MetricType.LongGauge or MetricType.LongSum or MetricType.LongSumNonMonotonic:
                dp.AsInt = p.GetSumLong();
                break;
            case MetricType.DoubleGauge or MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic:
                dp.AsDouble = p.GetSumDouble();
                break;
        }
        AddAttributes(dp.Attributes, p);
        return dp;
    }

    private static PMetric.HistogramDataPoint HistogramPoint(in MetricPoint p)
    {
        var dp = new PMetric.HistogramDataPoint
        {
            StartTimeUnixNano = ToUnixNanos(p.StartTime.UtcDateTime),
            TimeUnixNano      = ToUnixNanos(p.EndTime.UtcDateTime),
            Count             = (ulong)p.GetHistogramCount(),
            Sum               = p.GetHistogramSum(),
        };
        if (p.TryGetHistogramMinMaxValues(out var min, out var max))
        {
            dp.Min = min;
            dp.Max = max;
        }
        foreach (var b in p.GetHistogramBuckets())
        {
            dp.BucketCounts.Add((ulong)b.BucketCount);
            if (!double.IsPositiveInfinity(b.ExplicitBound))
                dp.ExplicitBounds.Add(b.ExplicitBound);
        }
        AddAttributes(dp.Attributes, p);
        return dp;
    }

    private static void AddAttributes(Google.Protobuf.Collections.RepeatedField<PCommon.KeyValue> target, in MetricPoint p)
    {
        foreach (var tag in p.Tags)
        {
            target.Add(new PCommon.KeyValue
            {
                Key   = tag.Key,
                Value = new PCommon.AnyValue { StringValue = tag.Value?.ToString() ?? string.Empty },
            });
        }
    }

    private static ulong ToUnixNanos(DateTime utc)
    {
        const long UnixEpochTicks = 621_355_968_000_000_000L;
        if (utc == default) return 0UL;
        long ticks = utc.Kind == DateTimeKind.Utc ? utc.Ticks : utc.ToUniversalTime().Ticks;
        long delta = ticks - UnixEpochTicks;
        return delta <= 0 ? 0UL : (ulong)delta * 100UL;
    }

    private static PCommon.Resource BuildResource(KamsoraApmOptions options)
    {
        var resource = new PCommon.Resource();
        resource.Attributes.Add(KvOf("service.name", options.ServiceName));
        if (!string.IsNullOrEmpty(options.ServiceNamespace))
            resource.Attributes.Add(KvOf("service.namespace", options.ServiceNamespace));
        resource.Attributes.Add(KvOf("service.version",
            options.ServiceVersion ?? typeof(KamsoraApmMetricExporter).Assembly.GetName().Version?.ToString() ?? "0.0.0"));
        resource.Attributes.Add(KvOf("kamsora.agent.version", KamsoraApmAgent.Version));
        resource.Attributes.Add(KvOf("host.name", Environment.MachineName));
        foreach (var (k, v) in options.ResourceAttributes)
            resource.Attributes.Add(KvOf(k, v));
        return resource;
    }

    private static PCommon.KeyValue KvOf(string key, string value) =>
        new() { Key = key, Value = new PCommon.AnyValue { StringValue = value ?? string.Empty } };

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
