// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Storage.Abstractions;

/// <summary>Bulk-insert path for <see cref="ProfileRow"/> batches. Used by the Collector's profile flusher.</summary>
public interface IProfileWriter
{
    Task WriteAsync(IReadOnlyList<ProfileRow> rows, CancellationToken cancellationToken);
}

/// <summary>Read-side queries over <c>kamsora_apm.profiles</c> for the ProfilesPage.</summary>
public interface IProfileReader
{
    /// <summary>
    /// Catalog query: one row per (service, profile_kind), most-recent-first within
    /// the window. Lets the dashboard render the "what profiles do we have" picker.
    /// </summary>
    Task<IReadOnlyList<ProfileCatalogEntry>> ListProfilesAsync(
        Guid tenantId, DateTime? fromUtc, DateTime? toUtc, string? serviceName, string? profileKind,
        int limit, CancellationToken ct);

    /// <summary>
    /// Fetch the raw pprof bytes for one profile. Identified by the (tenant, service,
    /// kind, start_timestamp) key - that uniquely pinpoints a capture given the table's
    /// ORDER BY.
    /// </summary>
    Task<ProfileBlob?> GetProfileBlobAsync(
        Guid tenantId, string serviceName, string profileKind, DateTime startUtc, CancellationToken ct);
}

public sealed record ProfileCatalogEntry(
    DateTime StartUtc,
    string   ServiceName,
    string   ProfileKind,
    double   DurationSeconds,
    long     SampleCount,
    long     PprofBytes,             // size in bytes - useful for diagnostics
    string   TriggerTraceIdHex,
    string   AgentVersion);

public sealed record ProfileBlob(
    DateTime StartUtc,
    string   ServiceName,
    string   ProfileKind,
    double   DurationSeconds,
    long     SampleCount,
    byte[]   PprofBytes);
