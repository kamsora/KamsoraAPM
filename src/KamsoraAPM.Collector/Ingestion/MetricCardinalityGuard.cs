// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// Per-tenant cardinality circuit-breaker for metric ingestion. Tracks the
/// distinct metric names seen per tenant inside a rolling 5-minute window;
/// once a tenant crosses <see cref="MaxDistinctMetricNames"/>, points for
/// NEW names are rejected until the window rolls. Protects the
/// metrics_minutely_rollup primary key (and dashboard query latency) from a
/// runaway label-explosion bug in a customer app - e.g. a metric name built
/// with a user id in it.
/// </summary>
public sealed class MetricCardinalityGuard
{
    private const int MaxDistinctMetricNames = 2000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, TenantWindow> _tenants = new();
    private long _rejectedTotal;

    /// <summary>Total points rejected since process start - surfaced on /stats.</summary>
    public long RejectedTotal => Interlocked.Read(ref _rejectedTotal);

    /// <summary>
    /// True when the point may be ingested. False when the tenant has
    /// exhausted its distinct-name budget for the current window and
    /// <paramref name="metricName"/> is not one of the already-seen names.
    /// </summary>
    public bool TryAdmit(Guid tenantId, string metricName)
    {
        var window = _tenants.GetOrAdd(tenantId, static _ => new TenantWindow());

        lock (window)
        {
            var now = DateTime.UtcNow;
            if (now - window.StartedUtc > Window)
            {
                window.Names.Clear();
                window.StartedUtc = now;
            }

            if (window.Names.Contains(metricName)) return true;
            if (window.Names.Count >= MaxDistinctMetricNames)
            {
                Interlocked.Increment(ref _rejectedTotal);
                return false;
            }

            window.Names.Add(metricName);
            return true;
        }
    }

    private sealed class TenantWindow
    {
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    }
}
