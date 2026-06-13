// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.AspNetCore.Http;

namespace KamsoraAPM.Agent.Internal.ConsumerExtraction;

/// <summary>
/// Extracts a stable, low-cardinality identifier for the consumer of an
/// inbound HTTP request. Implementations must be safe to call on the
/// request's hot path and must NEVER throw — failures should return null.
///
/// The resulting string is stored on the span's <c>kamsora.consumer.id</c>
/// attribute and rolled up by the M6 consumer analytics dashboards.
/// </summary>
public interface IConsumerExtractor
{
    /// <summary>
    /// Returns the consumer id for the request, or <see langword="null"/>
    /// if no consumer can be identified (the span will be rolled up under
    /// the synthetic "anonymous" bucket).
    /// </summary>
    string? Extract(HttpRequest request);
}
