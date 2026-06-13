// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>Bulk-insert path for <see cref="LogRow"/> batches. Used by the Collector's log flusher.</summary>
public interface ILogWriter
{
    Task WriteAsync(IReadOnlyList<LogRow> rows, CancellationToken cancellationToken);
}

/// <summary>Bulk-insert path for <see cref="MetricPointRow"/> batches.</summary>
public interface IMetricWriter
{
    Task WriteAsync(IReadOnlyList<MetricPointRow> rows, CancellationToken cancellationToken);
}
