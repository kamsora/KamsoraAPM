// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Channels;
using KamsoraAPM.Collector.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// Generic host-row flusher. Drains a typed <see cref="ChannelReader{TRow}"/>
/// in batches and writes via the supplied delegate (which is bound to one of
/// the four <c>IHostSnapshotWriter.Write*Async</c> overloads at DI time).
///
/// One instance per host table (<c>host_cpu_memory</c>, <c>host_disks</c>,
/// <c>host_networks</c>, <c>host_processes</c>) so each table gets its own
/// independent batching + retry pipeline.
/// </summary>
internal sealed class BatchFlusherHostedService<TRow> : BackgroundService
    where TRow : class
{
    public delegate Task WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken);

    private readonly ChannelReader<TRow> _reader;
    private readonly WriteAsync _write;
    private readonly CollectorOptions _options;
    private readonly ILogger<BatchFlusherHostedService<TRow>> _logger;
    private readonly ResiliencePipeline _retry;
    private readonly string _label;

    public BatchFlusherHostedService(
        ChannelReader<TRow> reader,
        WriteAsync write,
        IOptions<CollectorOptions> options,
        ILogger<BatchFlusherHostedService<TRow>> logger)
    {
        _reader  = reader;
        _write   = write;
        _options = options.Value;
        _logger  = logger;
        _label   = typeof(TRow).Name;

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
        var batch = new List<TRow>(capacity: _options.MaxFlushBatchSize);

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
                _logger.LogError(ex, "KamsoraAPM Collector: flusher<{Row}> loop crashed; continuing.", _label);
                batch.Clear();
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        await FinalDrainAsync().ConfigureAwait(false);
    }

    private async Task FlushAsync(List<TRow> rows, CancellationToken cancellationToken)
    {
        try
        {
            await _retry.ExecuteAsync(
                async ct => await _write(rows, ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "KamsoraAPM Collector: persistent flush<{Row}> failure; dropping batch of {Count} row(s).",
                _label, rows.Count);
        }
    }

    private async Task FinalDrainAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batch = new List<TRow>(capacity: _options.MaxFlushBatchSize);

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
            _logger.LogInformation("KamsoraAPM Collector: flusher<{Row}> stopped.", _label);
        }
    }
}
