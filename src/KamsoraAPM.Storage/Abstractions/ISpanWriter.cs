// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>
/// Persists batches of spans to the time-series store. Implementations must
/// be safe for concurrent invocation but should batch internally for throughput.
/// </summary>
public interface ISpanWriter
{
    /// <summary>
    /// Insert the given spans. Implementations should throw on persistent
    /// failure so the caller can decide whether to retry or drop the batch.
    /// </summary>
    Task WriteAsync(IReadOnlyList<SpanRow> rows, CancellationToken cancellationToken);
}
