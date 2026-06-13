// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("KamsoraAPM.Agent.Tests")]
[assembly: InternalsVisibleTo("KamsoraAPM.Integration.Tests")]
[assembly: InternalsVisibleTo("KamsoraAPM.Load.Tests")]

namespace KamsoraAPM.Agent;

/// <summary>
/// Marker type for the KamsoraAPM Agent assembly. Real instrumentation
/// surface (middleware, options, channel-based flusher) lands in M1/M2.
/// </summary>
public static class KamsoraApmAgent
{
    /// <summary>
    /// Semantic version of this Agent build. Reported in the
    /// <c>kamsora.agent.version</c> resource attribute.
    /// </summary>
    public const string Version = "0.1.0-m0";
}
