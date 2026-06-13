// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel.DataAnnotations;

namespace KamsoraAPM.Storage.Options;

/// <summary>Connection settings for the KamsoraAPM PostgreSQL metadata store.</summary>
public sealed class PostgresOptions
{
    /// <summary>
    /// Npgsql connection string, e.g.
    /// <c>Host=localhost;Port=5432;Database=kamsora_apm;Username=kamsora;Password=...</c>.
    /// </summary>
    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Per-statement command timeout. Defaults to 10 s.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
