// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("KamsoraAPM.Storage.Tests")]
[assembly: InternalsVisibleTo("KamsoraAPM.Collector.Tests")]
[assembly: InternalsVisibleTo("KamsoraAPM.Dashboard.Api.Tests")]
[assembly: InternalsVisibleTo("KamsoraAPM.Integration.Tests")]

namespace KamsoraAPM.Storage;

/// <summary>
/// Marker type for the KamsoraAPM Storage assembly. ClickHouse + raw-ADO.NET
/// PostgreSQL repositories land in M1.
/// </summary>
public static class KamsoraApmStorage
{
    /// <summary>Schema version recognised by this assembly.</summary>
    public const int SchemaVersion = 1;
}
