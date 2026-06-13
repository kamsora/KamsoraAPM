// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Channels;
using KamsoraAPM.Collector.Options;
using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// Drains the Collector's in-process span channel and persists batches to
/// ClickHouse via <see cref="ISpanWriter"/>. Implements bounded batching,
/// transient-failure retries, and a final-flush deadline on shutdown.
/// </summary>
internal sealed class SpanFlusherHostedService : BackgroundService
{
    private readonly ChannelReader<SpanRow> _reader;
    private readonly ISpanWriter _writer;
    private readonly CollectorOptions _options;
    private readonly ILogger<SpanFlusherHostedService> _logger;
    private readonly ResiliencePipeline _retry;

    public SpanFlusherHostedService(
        ChannelReader<SpanRow> reader,
        ISpanWriter writer,
        IOptions<CollectorOptions> options,
        ILogger<SpanFlusherHostedService> logger)
    {
        _reader  = reader;
        _writer  = writer;
        _options = options.Value;
        _logger  = logger;

        _retry = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType      = DelayBackoffType.Exponential,
                UseJitter        = true,
                Delay            = TimeSpan.FromMilliseconds(250),
                MaxDelay         = TimeSpan.FromSeconds(5),
            })
            .Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<SpanRow>(capacity: _options.MaxFlushBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                    break;

                using var batchTimer = new CancellationTokenSource(_options.FlushInterval);
                using var linked     = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, batchTimer.Token);

                while (batch.Count < _options.MaxFlushBatchSize && _reader.TryRead(out var row))
                    batch.Add(row);

                if (batch.Count < _options.MaxFlushBatchSize)
                {
                    try
                    {
                        await foreach (var row in _reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                        {
                            batch.Add(row);
                            if (batch.Count >= _options.MaxFlushBatchSize) break;
                        }
                    }
                    catch (OperationCanceledException) when (batchTimer.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    {
                        // Batch interval elapsed - flush partial batch.
                    }
                }

                if (batch.Count > 0)
                {
                    await FlushAsync(batch, stoppingToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KamsoraAPM Collector: flusher loop crashed; continuing.");
                batch.Clear();
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        await FinalDrainAsync().ConfigureAwait(false);
    }

    private async Task FlushAsync(List<SpanRow> rows, CancellationToken cancellationToken)
    {
        try
        {
            await _retry.ExecuteAsync(
                async ct => await _writer.WriteAsync(rows, ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "KamsoraAPM Collector: persistent flush failure; dropping batch of {Count} span(s).",
                rows.Count);
        }
    }

    private async Task FinalDrainAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batch = new List<SpanRow>(capacity: _options.MaxFlushBatchSize);

        try
        {
            while (_reader.TryRead(out var row))
            {
                batch.Add(row);
                if (batch.Count < _options.MaxFlushBatchSize) continue;

                await FlushAsync(batch, deadline.Token).ConfigureAwait(false);
                batch.Clear();
                if (deadline.IsCancellationRequested) break;
            }

            if (batch.Count > 0 && !deadline.IsCancellationRequested)
                await FlushAsync(batch, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown deadline reached. Drop remainder.
        }
        finally
        {
            _logger.LogInformation("KamsoraAPM Collector: flusher stopped.");
        }
    }
}
