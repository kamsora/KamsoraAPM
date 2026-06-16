// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Agent.Internal;

/// <summary>
/// Marks the current async flow as "inside the Agent's own export path" so the
/// <see cref="KamsoraApmActivityListener"/> can skip the gRPC / HTTP activities
/// the exporters create while shipping telemetry to the Collector.
///
/// <para>
/// The listener captures EVERY <see cref="System.Diagnostics.ActivitySource"/>
/// by default. Without this guard the export RPCs would themselves be traced and
/// fed straight back into the export queue - a self-sustaining loop that emits
/// telemetry even with zero application traffic.
/// </para>
/// </summary>
internal static class AgentSelfTrace
{
    private static readonly AsyncLocal<bool> Inside = new();

    /// <summary>True while the current async flow is inside an exporter call.</summary>
    public static bool IsExporting => Inside.Value;

    /// <summary>Suppress self-capture for the lifetime of the returned scope.</summary>
    public static Scope Suppress() => new();

    internal readonly struct Scope : IDisposable
    {
        private readonly bool _previous;

        public Scope()
        {
            _previous = Inside.Value;
            Inside.Value = true;
        }

        public void Dispose() => Inside.Value = _previous;
    }
}
