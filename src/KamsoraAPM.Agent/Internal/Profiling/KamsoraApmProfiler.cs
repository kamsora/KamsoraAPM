// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using KamsoraAPM.Agent.Options;
using KamsoraAPM.Contracts.Profiles.V1;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KamsoraAPM.Agent.Internal.Profiling;

/// <summary>
/// Periodically captures a short CPU sampling profile of the host process via
/// EventPipe, converts it to pprof, and hands it to
/// <see cref="KamsoraApmProfileExporter"/> for gRPC delivery.
///
/// Loop shape:
/// <code>
///   while (!stopping)
///   {
///     await Delay(ProfilingInterval);
///     captureNetTrace(ProfilingDuration);
///     pprof = convert(captureFile);
///     await exportAsync(pprof);
///   }
/// </code>
/// </summary>
internal sealed class KamsoraApmProfiler : BackgroundService
{
    // Microsoft-DotNETCore-SampleProfiler emits one Thread/Sample event per
    // running thread every ~9.7ms. That's our cheap, always-available CPU
    // signal - no symbol-rundown beyond what TraceLog reconstructs from
    // module load events.
    private const string SampleProfilerProvider = "Microsoft-DotNETCore-SampleProfiler";

    private readonly KamsoraApmOptions _options;
    private readonly KamsoraApmProfileExporter _exporter;
    private readonly ILogger<KamsoraApmProfiler> _logger;

    public KamsoraApmProfiler(
        IOptions<KamsoraApmOptions> options,
        KamsoraApmProfileExporter exporter,
        ILogger<KamsoraApmProfiler> logger)
    {
        _options  = options.Value;
        _exporter = exporter;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableProfiling)
        {
            _logger.LogInformation("KamsoraAPM Agent: continuous profiling disabled via options.");
            return;
        }

        var interval = ClampPositive(_options.ProfilingInterval, fallback: TimeSpan.FromSeconds(60));
        var duration = ClampPositive(_options.ProfilingDuration, fallback: TimeSpan.FromSeconds(10));
        if (duration >= interval)
        {
            // A capture longer than the interval means we'd be profiling
            // continuously with no idle gap - defensively shrink the capture
            // so the loop still has breathing room.
            duration = TimeSpan.FromTicks(interval.Ticks / 2);
            _logger.LogWarning(
                "KamsoraAPM Agent: ProfilingDuration >= ProfilingInterval; clamping duration to {Duration}.",
                duration);
        }

        _logger.LogInformation(
            "KamsoraAPM Agent: profiler started (interval={Interval}, duration={Duration}, pid={Pid}).",
            interval, duration, Environment.ProcessId);

        // Stagger the first capture slightly so we don't sample during the
        // app's own startup burst (JIT, configuration load, host warmup).
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CaptureOneAsync(duration, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KamsoraAPM Agent: profile capture failed; continuing.");
            }

            try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CaptureOneAsync(TimeSpan duration, CancellationToken ct)
    {
        var nettraceFile = Path.Combine(Path.GetTempPath(),
            $"kamsora-apm-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.nettrace");

        var startUtc = DateTime.UtcNow;
        try
        {
            await CaptureNetTraceAsync(nettraceFile, duration, ct).ConfigureAwait(false);

            var (pprofBytes, sampleCount) = NetTraceToPprofConverter.Convert(nettraceFile, startUtc, duration);

            if (sampleCount == 0)
            {
                _logger.LogDebug("KamsoraAPM Agent: profile capture produced 0 samples (idle process); skipping export.");
                return;
            }

            var capture = new ProfileCapture(
                StartUtc:       startUtc,
                Duration:       duration,
                Kind:           ProfileKind.Cpu,
                SampleCount:    sampleCount,
                PprofBytes:     pprofBytes,
                TriggerTraceId: null);

            await _exporter.ExportAsync(capture, ct).ConfigureAwait(false);

            _logger.LogDebug(
                "KamsoraAPM Agent: shipped CPU profile - samples={Count}, pprof={Bytes} B, duration={Duration}.",
                sampleCount, pprofBytes.Length, duration);
        }
        finally
        {
            try { if (File.Exists(nettraceFile)) File.Delete(nettraceFile); }
            catch (IOException)                  { /* benign */ }
            catch (UnauthorizedAccessException) { /* benign */ }
        }
    }

    private static async Task CaptureNetTraceAsync(string outputPath, TimeSpan duration, CancellationToken ct)
    {
        // Connect to our own runtime via the diagnostic port. requestRundown
        // = true forces method/module info to be flushed into the stream
        // so TraceLog can resolve managed call stacks on the way out.
        var client    = new DiagnosticsClient(Environment.ProcessId);
        var providers = new[]
        {
            new EventPipeProvider(SampleProfilerProvider, System.Diagnostics.Tracing.EventLevel.Informational, keywords: 0),
        };

        using var session = client.StartEventPipeSession(providers, requestRundown: true);
        await using var fs = File.Create(outputPath);

        // Copy until either the duration elapses or the host is shutting down.
        using var captureWindow = new CancellationTokenSource(duration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, captureWindow.Token);

        try
        {
            await session.EventStream.CopyToAsync(fs, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (captureWindow.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Expected - the capture window closed normally.
        }

        // Stop the session cleanly (flushes rundown).
        try { session.Stop(); } catch { /* session may already be closed */ }
    }

    private static TimeSpan ClampPositive(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;
}
