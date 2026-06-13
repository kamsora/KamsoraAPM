// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Channels;
using KamsoraAPM.Agent.Options;
using KamsoraAPM.Contracts.Trace.V1;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Agent.Internal;

/// <summary>
/// Background flusher: continuously drains the in-process span channel and
/// exports batches to the Collector. Stops gracefully on host shutdown with
/// a bounded final-flush deadline.
/// </summary>
internal sealed class KamsoraApmExporterHostedService : BackgroundService
{
    private readonly ChannelReader<Span> _reader;
    private readonly KamsoraApmExporter _exporter;
    private readonly KamsoraApmOptions _options;
    private readonly ILogger<KamsoraApmExporterHostedService> _logger;

    public KamsoraApmExporterHostedService(
        ChannelReader<Span> reader,
        KamsoraApmExporter exporter,
        IOptions<KamsoraApmOptions> options,
        ILogger<KamsoraApmExporterHostedService> logger)
    {
        _reader   = reader;
        _exporter = exporter;
        _options  = options.Value;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<Span>(capacity: _options.MaxExportBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    break; // channel completed
                }

                using var batchTimer = new CancellationTokenSource(_options.ExportInterval);
                using var linked     = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, batchTimer.Token);

                while (batch.Count < _options.MaxExportBatchSize && _reader.TryRead(out var span))
                {
                    batch.Add(span);
                }

                if (batch.Count < _options.MaxExportBatchSize)
                {
                    // Wait up to ExportInterval for more items, then flush whatever we have.
                    try
                    {
                        await foreach (var span in _reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                        {
                            batch.Add(span);
                            if (batch.Count >= _options.MaxExportBatchSize) break;
                        }
                    }
                    catch (OperationCanceledException) when (batchTimer.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    {
                        // Batch timer fired — flush partial batch.
                    }
                }

                if (batch.Count > 0)
                {
                    await _exporter.ExportAsync(batch, stoppingToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KamsoraAPM Agent exporter loop encountered an unexpected error. Continuing.");
                batch.Clear();
                // Brief back-off to avoid hot-looping on a recurring failure.
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* shutdown */ }
            }
        }

        await FinalDrainAsync().ConfigureAwait(false);
    }

    private async Task FinalDrainAsync()
    {
        // Try to flush remaining items within 2 seconds. Anything still queued
        // after that is dropped — the host is shutting down and we can't block.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var batch = new List<Span>(capacity: _options.MaxExportBatchSize);

        try
        {
            while (_reader.TryRead(out var span))
            {
                batch.Add(span);
                if (batch.Count < _options.MaxExportBatchSize) continue;

                await _exporter.ExportAsync(batch, deadline.Token).ConfigureAwait(false);
                batch.Clear();
                if (deadline.IsCancellationRequested) break;
            }

            if (batch.Count > 0 && !deadline.IsCancellationRequested)
            {
                await _exporter.ExportAsync(batch, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown deadline reached. Accept the drop.
        }
        finally
        {
            _logger.LogInformation("KamsoraAPM Agent exporter stopped. Final-drain flushed {Count} span(s).", batch.Count);
        }
    }
}
