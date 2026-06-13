// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KamsoraAPM.Collector.Infrastructure;

/// <summary>
/// Enforces per-tenant data retention by dropping expired ClickHouse
/// partitions. The static table TTL (14 days) remains the upper bound; this
/// sweeper applies each tenant's <c>mastertenants.data_retention_days</c>
/// when it is SHORTER (e.g. a free-tier tenant on a 3-day plan).
///
/// Partition layout convention: every telemetry table partitions by
/// <c>(tenant_id, toYYYYMMDD(...))</c> or <c>(tenant_id, toYYYYMM(...))</c>,
/// so a partition is droppable purely from its name â€” no row scans.
/// Runs daily; first sweep ~5 minutes after startup.
/// </summary>
internal sealed class RetentionSweeperHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SweepEvery   = TimeSpan.FromHours(24);

    private readonly ClickHouseOptions _clickHouse;
    private readonly PostgresOptions _postgres;
    private readonly ILogger<RetentionSweeperHostedService> _logger;

    public RetentionSweeperHostedService(
        IOptions<ClickHouseOptions> clickHouse,
        IOptions<PostgresOptions> postgres,
        ILogger<RetentionSweeperHostedService> logger)
    {
        _clickHouse = clickHouse.Value;
        _postgres   = postgres.Value;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KamsoraAPM Collector: retention sweep failed; retrying next cycle.");
            }

            try { await Task.Delay(SweepEvery, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        var retentionByTenant = await LoadTenantRetentionAsync(ct).ConfigureAwait(false);
        if (retentionByTenant.Count == 0) return;

        await using var conn = new ClickHouseConnection(_clickHouse.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // All active partitions whose name follows the (uuid, yyyymmdd|yyyymm) tuple form.
        var partitions = new List<(string Table, string Partition)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT DISTINCT table, partition
                  FROM system.parts
                 WHERE database = 'kamsora_apm' AND active
                   AND partition LIKE '(%'";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                partitions.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        int dropped = 0;
        var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var (table, partition) in partitions)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryParsePartition(partition, out var tenantId, out var periodEndUtc)) continue;
            if (!retentionByTenant.TryGetValue(tenantId, out var retentionDays)) continue;

            var cutoff = todayUtc.AddDays(-retentionDays);
            if (periodEndUtc >= cutoff) continue;

            await using var drop = conn.CreateCommand();
            // Partition literal comes from system.parts verbatim â€” already in
            // tuple syntax â€” so it is interpolated, not parameterised (DDL
            // does not accept parameters). Values originate from ClickHouse
            // itself, not from user input.
            drop.CommandText = $"ALTER TABLE kamsora_apm.{table} DROP PARTITION {partition}";
            await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            dropped++;

            _logger.LogInformation(
                "KamsoraAPM Collector: retention sweep dropped {Table} partition {Partition} (tenant={Tenant}, retention={Days}d).",
                table, partition, tenantId, retentionDays);
        }

        _logger.LogInformation("KamsoraAPM Collector: retention sweep complete â€” {Dropped} partition(s) dropped.", dropped);
    }

    private async Task<Dictionary<Guid, int>> LoadTenantRetentionAsync(CancellationToken ct)
    {
        var map = new Dictionary<Guid, int>();
        await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT systenantuuid, data_retention_days FROM public.mastertenants WHERE status <> 'deleted'", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (Guid.TryParse(reader.GetString(0), out var id) && !reader.IsDBNull(1))
            {
                map[id] = Math.Max(1, reader.GetInt32(1));
            }
        }
        return map;
    }

    /// <summary>
    /// Parse "('a1b2c3d4-â€¦', 20260601)" or "('a1b2c3d4-â€¦', 202606)" into the
    /// tenant id and the END of the period the partition covers (a daily
    /// partition ends that day; a monthly partition ends the month's last day).
    /// </summary>
    internal static bool TryParsePartition(string partition, out Guid tenantId, out DateOnly periodEndUtc)
    {
        tenantId    = Guid.Empty;
        periodEndUtc = default;

        var inner = partition.Trim().TrimStart('(').TrimEnd(')');
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;

        if (!Guid.TryParse(parts[0].Trim('\''), out tenantId)) return false;

        var digits = parts[1].Trim('\'');
        if (digits.Length == 8 &&
            DateOnly.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            periodEndUtc = day;
            return true;
        }
        if (digits.Length == 6 &&
            int.TryParse(digits[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
            int.TryParse(digits[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
            month is >= 1 and <= 12)
        {
            periodEndUtc = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            return true;
        }
        return false;
    }
}
