// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Storage.ClickHouse;

namespace KamsoraAPM.Collector.Infrastructure;

/// <summary>
/// Applies pending ClickHouse schema migrations at Collector startup,
/// before any flusher begins writing. Registered FIRST among hosted
/// services — the host starts them sequentially in registration order,
/// so the schema is guaranteed current before ingestion begins.
/// </summary>
internal sealed class MigrationHostedService : IHostedService
{
    private readonly ClickHouseMigrationRunner _runner;
    private readonly ILogger<MigrationHostedService> _logger;

    public MigrationHostedService(ClickHouseMigrationRunner runner, ILogger<MigrationHostedService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("KamsoraAPM Collector: applying ClickHouse schema migrations…");
        await _runner.ApplyPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
