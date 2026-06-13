// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.ClickHouse;
using Testcontainers.PostgreSql;
using Xunit;

namespace KamsoraAPM.Integration.Tests.Fixtures;

/// <summary>
/// Brings up PostgreSQL + ClickHouse via Testcontainers, applies the
/// schema migrations from <c>deploy/sql/</c>, seeds a tenant + API key,
/// and hosts the Collector via <see cref="WebApplicationFactory{TEntryPoint}"/>
/// against those databases. Shared across all tests in the assembly.
/// </summary>
public sealed class KamsoraStackFixture : IAsyncLifetime
{
    private const string TenantSlug = "integration-test";
    private const string TenantName = "Integration Test Tenant";

    private PostgreSqlContainer _postgres = null!;
    private ClickHouseContainer _clickhouse = null!;
    private WebApplicationFactory<KamsoraAPM.Collector.Program> _collectorFactory = null!;

    public string PostgresConnectionString  => _postgres.GetConnectionString();
    public string ClickHouseConnectionString { get; private set; } = string.Empty;

    public string TenantUuid { get; private set; } = string.Empty;
    public string ApiKey     { get; private set; } = string.Empty;
    public string CollectorBaseAddress { get; private set; } = string.Empty;

    public HttpClient CollectorHttpClient => _collectorFactory.CreateClient();
    public IServiceProvider CollectorServices => _collectorFactory.Services;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("kamsora_apm")
            .WithUsername("kamsora")
            .WithPassword("kamsora_dev_only_change_me")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready -U kamsora -d kamsora_apm"))
            .Build();
        await _postgres.StartAsync().ConfigureAwait(false);

        _clickhouse = new ClickHouseBuilder()
            .WithImage("clickhouse/clickhouse-server:24.10")
            .WithDatabase("kamsora_apm")
            .WithUsername("kamsora")
            .WithPassword("kamsora_dev_only_change_me")
            .Build();
        await _clickhouse.StartAsync().ConfigureAwait(false);
        ClickHouseConnectionString = _clickhouse.GetConnectionString();

        await ApplyPostgresSchemaAsync().ConfigureAwait(false);
        await ApplyClickHouseSchemaAsync().ConfigureAwait(false);
        await SeedTenantAsync().ConfigureAwait(false);

        var configOverrides = new Dictionary<string, string?>
        {
            ["KamsoraApm:Postgres:ConnectionString"]   = PostgresConnectionString,
            ["KamsoraApm:ClickHouse:ConnectionString"] = ClickHouseConnectionString,
            ["KamsoraApm:Collector:QueueCapacity"]     = "10000",
            ["KamsoraApm:Collector:MaxFlushBatchSize"] = "256",
            ["KamsoraApm:Collector:FlushInterval"]     = "00:00:00.250",
        };

        _collectorFactory = new WebApplicationFactory<KamsoraAPM.Collector.Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configOverrides));
            b.UseSetting("ASPNETCORE_URLS", "http://127.0.0.1:0");
        });
        // Eagerly start the host so background services (the flusher) are running.
        _ = _collectorFactory.Server;
        CollectorBaseAddress = _collectorFactory.Server.BaseAddress.ToString();
    }

    public async Task DisposeAsync()
    {
        await _collectorFactory.DisposeAsync().ConfigureAwait(false);
        await _clickhouse.DisposeAsync().ConfigureAwait(false);
        await _postgres.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ApplyPostgresSchemaAsync()
    {
        var sqlRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy", "sql", "postgres");
        sqlRoot     = Path.GetFullPath(sqlRoot);

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync().ConfigureAwait(false);

        foreach (var file in Directory.EnumerateFiles(sqlRoot, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            var script = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(script, conn);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private async Task ApplyClickHouseSchemaAsync()
    {
        var sqlRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy", "sql", "clickhouse");
        sqlRoot     = Path.GetFullPath(sqlRoot);

        await using var conn = new ClickHouse.Client.ADO.ClickHouseConnection(_clickhouse.GetConnectionString());
        await conn.OpenAsync().ConfigureAwait(false);

        foreach (var file in Directory.EnumerateFiles(sqlRoot, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            var script = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            foreach (var stmt in SplitClickHouseStatements(script))
            {
                if (string.IsNullOrWhiteSpace(stmt)) continue;
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = stmt;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }

    private static readonly string[] StatementSeparators = [";\n", ";\r\n"];

    private static string[] SplitClickHouseStatements(string script)
    {
        // ClickHouse client doesn't accept multi-statement batches over HTTP.
        // Split on ';' at end of line. Good enough for our M0 schema files
        // which use one statement per CREATE.
        return script.Split(StatementSeparators, StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task SeedTenantAsync()
    {
        var apiKeyCleartext = "kapm_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var keyHash         = Pbkdf2Hash(apiKeyCleartext);
        var keyPrefix       = apiKeyCleartext[..8];

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync().ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand(
            "SELECT public.fn_api_post_mastertenants(@name, @slug, NULL, NULL, NULL, NULL, 'test:fixture')", conn))
        {
            cmd.Parameters.AddWithValue("name", TenantName);
            cmd.Parameters.AddWithValue("slug", TenantSlug);
            TenantUuid = (string)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
        }

        await using (var cmd = new NpgsqlCommand(
            "SELECT public.fn_api_post_masterapi_keys(@tenant, 'integration', @prefix, @hash, 'ingest', NULL, 'test:fixture')", conn))
        {
            cmd.Parameters.AddWithValue("tenant", TenantUuid);
            cmd.Parameters.AddWithValue("prefix", keyPrefix);
            cmd.Parameters.AddWithValue("hash",   keyHash);
            await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        }

        ApiKey = apiKeyCleartext;
    }

    private static string Pbkdf2Hash(string cleartext)
    {
        var salt    = RandomNumberGenerator.GetBytes(16);
        var derived = Rfc2898DeriveBytes.Pbkdf2(cleartext, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"$pbkdf2$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(derived)}";
    }
}
