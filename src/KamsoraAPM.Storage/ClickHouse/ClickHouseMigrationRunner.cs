// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using ClickHouse.Client.ADO;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Storage.ClickHouse;

/// <summary>
/// Applies the ClickHouse schema scripts embedded in this assembly
/// (deploy/sql/clickhouse/*.sql at build time) in lexical order, tracking
/// applied files in <c>kamsora_apm.schema_migrations</c>.
///
/// Replaces the docker-entrypoint-initdb.d mechanism, which only runs on an
/// EMPTY ClickHouse volume — upgrades on existing volumes silently skipped
/// new schema files. Every script is idempotent (CREATE TABLE IF NOT
/// EXISTS), so baselining an existing database is a safe no-op pass that
/// simply records the scripts as applied.
/// </summary>
public sealed class ClickHouseMigrationRunner
{
    private const string MigrationsTableDdl = @"
        CREATE TABLE IF NOT EXISTS kamsora_apm.schema_migrations
        (
            migration_name String,
            applied_at     DateTime64(3, 'UTC') DEFAULT now64(3)
        )
        ENGINE = MergeTree
        ORDER BY migration_name";

    private readonly ClickHouseOptions _options;
    private readonly ILogger<ClickHouseMigrationRunner> _logger;

    public ClickHouseMigrationRunner(IOptions<ClickHouseOptions> options, ILogger<ClickHouseMigrationRunner> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public async Task ApplyPendingAsync(CancellationToken ct)
    {
        // 1. Make sure the database itself exists — connect without a default
        //    database so this works on a completely blank ClickHouse server.
        var bootstrapCs = new ClickHouseConnectionStringBuilder(_options.ConnectionString) { Database = "default" };
        await using (var bootstrap = new ClickHouseConnection(bootstrapCs.ToString()))
        {
            await bootstrap.OpenAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(bootstrap, "CREATE DATABASE IF NOT EXISTS kamsora_apm", ct).ConfigureAwait(false);
        }

        await using var conn = new ClickHouseConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await ExecuteAsync(conn, MigrationsTableDdl, ct).ConfigureAwait(false);

        var applied = await LoadAppliedAsync(conn, ct).ConfigureAwait(false);
        var scripts = LoadEmbeddedScripts();

        int ran = 0;
        foreach (var (name, sql) in scripts)
        {
            if (applied.Contains(name)) continue;
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation("ClickHouse migration: applying {Name}…", name);
            foreach (var statement in SplitStatements(sql))
            {
                await ExecuteAsync(conn, statement, ct).ConfigureAwait(false);
            }

            await using (var record = conn.CreateCommand())
            {
                record.CommandText = "INSERT INTO kamsora_apm.schema_migrations (migration_name) VALUES ({n:String})";
                var p = record.CreateParameter();
                p.ParameterName = "n";
                p.Value         = name;
                record.Parameters.Add(p);
                await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            ran++;
        }

        _logger.LogInformation(
            "ClickHouse migrations: {Ran} applied, {Skipped} already present.",
            ran, scripts.Count - ran);
    }

    private static async Task ExecuteAsync(ClickHouseConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> LoadAppliedAsync(ClickHouseConnection conn, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT migration_name FROM kamsora_apm.schema_migrations";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            set.Add(reader.GetString(0));
        }
        return set;
    }

    /// <summary>Embedded migration scripts ordered by resource name (001_, 010_, …).</summary>
    private static List<(string Name, string Sql)> LoadEmbeddedScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var list = new List<(string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(static n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(static n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var sr = new StreamReader(stream);
            // Resource names look like "KamsoraAPM.Storage.Migrations.010_spans.sql";
            // track just the trailing "010_spans.sql".
            const string marker = ".Migrations.";
            var idx = resource.IndexOf(marker, StringComparison.Ordinal);
            var shortName = idx >= 0 ? resource[(idx + marker.Length)..] : resource;
            list.Add((shortName, sr.ReadToEnd()));
        }
        return list;
    }

    /// <summary>
    /// Split a script into individual statements. ClickHouse.Client does not
    /// support multi-statement commands. Line comments are stripped first so
    /// a ';' inside a comment can't truncate a statement.
    /// </summary>
    internal static IEnumerable<string> SplitStatements(string sql)
    {
        var withoutComments = string.Join('\n',
            sql.Split('\n').Select(static line =>
            {
                var idx = line.IndexOf("--", StringComparison.Ordinal);
                return idx >= 0 ? line[..idx] : line;
            }));

        foreach (var raw in withoutComments.Split(';'))
        {
            var statement = raw.Trim();
            if (statement.Length > 0) yield return statement;
        }
    }
}
