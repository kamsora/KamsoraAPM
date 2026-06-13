// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel.DataAnnotations;

namespace KamsoraAPM.Storage.Options;

/// <summary>Connection settings for the KamsoraAPM ClickHouse telemetry store.</summary>
public sealed class ClickHouseOptions
{
    /// <summary>
    /// Full ADO.NET connection string for ClickHouse, e.g.
    /// <c>Host=localhost;Port=8123;Database=kamsora_apm;User=kamsora;Password=...</c>.
    /// </summary>
    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Schema (database) name. Defaults to <c>kamsora_apm</c>.</summary>
    public string Database { get; set; } = "kamsora_apm";

    /// <summary>Per-statement command timeout. Defaults to 30 s.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
