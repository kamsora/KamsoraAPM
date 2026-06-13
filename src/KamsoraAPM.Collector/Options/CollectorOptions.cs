// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel.DataAnnotations;

namespace KamsoraAPM.Collector.Options;

/// <summary>Runtime knobs for the Collector ingestion pipeline.</summary>
public sealed class CollectorOptions
{
    /// <summary>Bounded capacity of the in-process ingestion buffer. Default 100,000.</summary>
    [Range(100, 10_000_000)]
    public int QueueCapacity { get; set; } = 100_000;

    /// <summary>Maximum rows flushed per ClickHouse bulk-copy. Default 5,000.</summary>
    [Range(1, 100_000)]
    public int MaxFlushBatchSize { get; set; } = 5_000;

    /// <summary>Maximum wait time before flushing a partial batch. Default 1 s.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);
}
