// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Runtime.Versioning;
using KamsoraAPM.Contracts.Host.V1;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// Windows network sampler. Pulls per-NIC throughput + error counters from the
/// "Network Interface" performance category. Loopback and tunneling pseudo
/// interfaces are filtered out so the sampler reports only physical / VPN NICs.
/// Counters are cached per instance and refreshed when an interface appears or
/// disappears between samples.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkSampler : INetworkSampler, IDisposable
{
    private const string Category = "Network Interface";

    private static readonly string[] SkipPatterns =
    {
        "loopback",
        "isatap",
        "teredo",
        "wan miniport",
    };

    private readonly ILogger<WindowsNetworkSampler> _logger;
    private readonly Dictionary<string, NicCounters> _counters = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public WindowsNetworkSampler(ILogger<WindowsNetworkSampler> logger)
    {
        _logger = logger;
    }

    public ValueTask<IReadOnlyList<NetworkSample>> SampleAsync(CancellationToken cancellationToken)
    {
        var results = new List<NetworkSample>(8);

        string[] instances;
        try
        {
            var category = new PerformanceCounterCategory(Category);
            instances = category.GetInstanceNames();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM HostMonitor: failed to enumerate network interfaces.");
            return ValueTask.FromResult<IReadOnlyList<NetworkSample>>(results);
        }

        // Drop stale cached counters whose interfaces have disappeared.
        var alive = new HashSet<string>(instances, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _counters.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _counters[key].Dispose();
            _counters.Remove(key);
        }

        foreach (var instance in instances)
        {
            if (ShouldSkip(instance)) continue;
            var c = GetOrCreateCounters(instance);

            results.Add(new NetworkSample
            {
                InterfaceName   = instance,
                RxBytesPerSec   = ReadULong(c.RxBytes),
                TxBytesPerSec   = ReadULong(c.TxBytes),
                RxPacketsPerSec = ReadULong(c.RxPackets),
                TxPacketsPerSec = ReadULong(c.TxPackets),
                RxErrors        = ReadULong(c.RxErrors),
                TxErrors        = ReadULong(c.TxErrors),
            });
        }

        return ValueTask.FromResult<IReadOnlyList<NetworkSample>>(results);
    }

    private static bool ShouldSkip(string interfaceName)
    {
        foreach (var pat in SkipPatterns)
            if (interfaceName.Contains(pat, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private NicCounters GetOrCreateCounters(string instance)
    {
        if (_counters.TryGetValue(instance, out var existing)) return existing;

        var c = new NicCounters(
            RxBytes:   new PerformanceCounter(Category, "Bytes Received/sec",         instance, readOnly: true),
            TxBytes:   new PerformanceCounter(Category, "Bytes Sent/sec",             instance, readOnly: true),
            RxPackets: new PerformanceCounter(Category, "Packets Received/sec",       instance, readOnly: true),
            TxPackets: new PerformanceCounter(Category, "Packets Sent/sec",           instance, readOnly: true),
            RxErrors:  new PerformanceCounter(Category, "Packets Received Errors",    instance, readOnly: true),
            TxErrors:  new PerformanceCounter(Category, "Packets Outbound Errors",    instance, readOnly: true));

        _ = c.RxBytes.NextValue();
        _ = c.TxBytes.NextValue();
        _ = c.RxPackets.NextValue();
        _ = c.TxPackets.NextValue();
        _ = c.RxErrors.NextValue();
        _ = c.TxErrors.NextValue();

        _counters[instance] = c;
        return c;
    }

    private static ulong ReadULong(PerformanceCounter counter)
    {
        try
        {
            var v = counter.NextValue();
            return v <= 0 ? 0UL : (ulong)v;
        }
        catch
        {
            return 0UL;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var c in _counters.Values) c.Dispose();
        _counters.Clear();
    }

    private sealed record NicCounters(
        PerformanceCounter RxBytes,
        PerformanceCounter TxBytes,
        PerformanceCounter RxPackets,
        PerformanceCounter TxPackets,
        PerformanceCounter RxErrors,
        PerformanceCounter TxErrors) : IDisposable
    {
        public void Dispose()
        {
            RxBytes.Dispose(); TxBytes.Dispose();
            RxPackets.Dispose(); TxPackets.Dispose();
            RxErrors.Dispose(); TxErrors.Dispose();
        }
    }
}
