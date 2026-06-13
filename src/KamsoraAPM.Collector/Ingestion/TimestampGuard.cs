// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// Clamps inbound telemetry timestamps to a sane window around the
/// Collector's clock. An Agent with a badly skewed clock (unsynced
/// container, paused VM) would otherwise write rows into far-future or
/// long-expired partitions — breaking TTL eviction, retention sweeps,
/// and "last 15 min" dashboard queries in confusing ways.
/// </summary>
internal static class TimestampGuard
{
    private static readonly TimeSpan MaxPast   = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxFuture = TimeSpan.FromMinutes(5);

    private const long UnixEpochTicks = 621_355_968_000_000_000L;

    /// <summary>Clamp a single point-in-time stamp (logs, metric points) into the window.</summary>
    public static ulong ClampNanos(ulong unixNanos)
    {
        var (minNs, maxNs) = Window();
        if (unixNanos < minNs) return minNs;
        if (unixNanos > maxNs) return maxNs;
        return unixNanos;
    }

    /// <summary>
    /// Clamp a span's start/end pair by SHIFTING both by the same delta when
    /// the start falls outside the window — duration is preserved, only the
    /// wall-clock placement moves.
    /// </summary>
    public static (ulong StartNanos, ulong EndNanos) ClampSpan(ulong startNanos, ulong endNanos)
    {
        var (minNs, maxNs) = Window();
        if (startNanos >= minNs && startNanos <= maxNs) return (startNanos, endNanos);

        var duration = endNanos >= startNanos ? endNanos - startNanos : 0UL;
        var clampedStart = startNanos < minNs ? minNs : maxNs;
        return (clampedStart, clampedStart + duration);
    }

    private static (ulong MinNs, ulong MaxNs) Window()
    {
        var nowNs = (ulong)((DateTime.UtcNow.Ticks - UnixEpochTicks) * 100L);
        var minNs = nowNs - (ulong)(MaxPast.Ticks * 100L);
        var maxNs = nowNs + (ulong)(MaxFuture.Ticks * 100L);
        return (minNs, maxNs);
    }
}
