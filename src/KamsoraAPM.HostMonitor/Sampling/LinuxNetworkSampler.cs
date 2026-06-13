// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Host.V1;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// Linux <see cref="INetworkSampler"/>. Reads cumulative per-interface counters
/// from <c>/proc/net/dev</c> and converts the byte/packet counters to
/// per-second rates using the delta since the previous sample. Loopback and the
/// common virtual/bridge/container interfaces are skipped. Error counters are
/// reported cumulative, matching the Windows sampler.
/// </summary>
public sealed class LinuxNetworkSampler : INetworkSampler
{
    private static readonly string[] SkipPrefixes =
        { "lo", "veth", "docker", "br-", "virbr", "cni", "flannel", "kube", "cali", "tap" };

    private readonly ILogger<LinuxNetworkSampler> _logger;
    private readonly Dictionary<string, Counters> _previous = new(StringComparer.Ordinal);
    private DateTime _lastUtc = DateTime.UtcNow;

    public LinuxNetworkSampler(ILogger<LinuxNetworkSampler> logger) => _logger = logger;

    public ValueTask<IReadOnlyList<NetworkSample>> SampleAsync(CancellationToken cancellationToken)
    {
        var results = new List<NetworkSample>(8);
        var nowUtc  = DateTime.UtcNow;
        var elapsed = Math.Max(0.001, (nowUtc - _lastUtc).TotalSeconds);

        try
        {
            foreach (var line in File.ReadLines("/proc/net/dev"))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue; // the two header rows have no "iface:" segment
                var name = line[..colon].Trim();
                if (name.Length == 0 || ShouldSkip(name)) continue;

                var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // RX: 0 bytes 1 packets 2 errs 3 drop 4 fifo 5 frame 6 compressed 7 multicast
                // TX: 8 bytes 9 packets 10 errs 11 drop 12 fifo 13 colls 14 carrier 15 compressed
                if (f.Length < 16) continue;
                ulong U(int i) => ulong.TryParse(f[i], out var v) ? v : 0UL;
                var cur = new Counters(U(0), U(8), U(1), U(9), U(2), U(10));

                var sample = new NetworkSample
                {
                    InterfaceName = name,
                    RxErrors      = cur.RxErr,
                    TxErrors      = cur.TxErr,
                };
                if (_previous.TryGetValue(name, out var prev))
                {
                    sample.RxBytesPerSec   = Rate(cur.RxBytes,   prev.RxBytes,   elapsed);
                    sample.TxBytesPerSec   = Rate(cur.TxBytes,   prev.TxBytes,   elapsed);
                    sample.RxPacketsPerSec = Rate(cur.RxPackets, prev.RxPackets, elapsed);
                    sample.TxPacketsPerSec = Rate(cur.TxPackets, prev.TxPackets, elapsed);
                }

                _previous[name] = cur;
                results.Add(sample);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM HostMonitor: failed to read /proc/net/dev.");
        }

        _lastUtc = nowUtc;
        return ValueTask.FromResult<IReadOnlyList<NetworkSample>>(results);
    }

    private static ulong Rate(ulong cur, ulong prev, double elapsed)
        => cur > prev ? (ulong)((cur - prev) / elapsed) : 0UL;

    private static bool ShouldSkip(string name)
    {
        foreach (var prefix in SkipPrefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private readonly record struct Counters(
        ulong RxBytes, ulong TxBytes, ulong RxPackets, ulong TxPackets, ulong RxErr, ulong TxErr);
}
