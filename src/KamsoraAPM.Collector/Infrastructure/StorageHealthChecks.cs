// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KamsoraAPM.Collector.Infrastructure;

/// <summary>Readiness check: can we reach ClickHouse and run a trivial query?</summary>
internal sealed class ClickHouseHealthCheck : IHealthCheck
{
    private readonly ClickHouseOptions _options;

    public ClickHouseHealthCheck(IOptions<ClickHouseOptions> options) => _options = options.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new ClickHouseConnection(_options.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("clickhouse reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("clickhouse unreachable", ex);
        }
    }
}

/// <summary>Readiness check: can we reach PostgreSQL and run a trivial query?</summary>
internal sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly PostgresOptions _options;

    public PostgresHealthCheck(IOptions<PostgresOptions> options) => _options = options.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_options.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("postgres reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("postgres unreachable", ex);
        }
    }
}
