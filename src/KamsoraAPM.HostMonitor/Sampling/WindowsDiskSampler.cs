// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Runtime.Versioning;
using KamsoraAPM.Contracts.Host.V1;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// Windows disk sampler. Capacity comes from <see cref="DriveInfo"/>; throughput
/// comes from the LogicalDisk performance counter set
/// (Disk Reads/sec, Disk Writes/sec, Disk Read Bytes/sec, Disk Write Bytes/sec).
/// Counter objects are cached per drive and reused so Windows can compute the
/// per-second deltas correctly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskSampler : IDiskSampler, IDisposable
{
    private const string Category = "LogicalDisk";

    private readonly ILogger<WindowsDiskSampler> _logger;
    private readonly Dictionary<string, DriveCounters> _counters = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public WindowsDiskSampler(ILogger<WindowsDiskSampler> logger)
    {
        _logger = logger;
    }

    public ValueTask<IReadOnlyList<DiskSample>> SampleAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiskSample>(8);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

            // LogicalDisk counters use the drive letter without the trailing slash, e.g. "C:".
            var letter = drive.Name.TrimEnd('\\');
            var counters = GetOrCreateCounters(letter);

            results.Add(new DiskSample
            {
                Device           = letter,
                Mountpoint       = drive.Name,
                TotalBytes       = (ulong)drive.TotalSize,
                UsedBytes        = (ulong)(drive.TotalSize - drive.AvailableFreeSpace),
                ReadsPerSec      = ReadULong(counters.Reads),
                WritesPerSec     = ReadULong(counters.Writes),
                ReadBytesPerSec  = ReadULong(counters.ReadBytes),
                WriteBytesPerSec = ReadULong(counters.WriteBytes),
            });
        }

        return ValueTask.FromResult<IReadOnlyList<DiskSample>>(results);
    }

    private DriveCounters GetOrCreateCounters(string instance)
    {
        if (_counters.TryGetValue(instance, out var existing)) return existing;

        var c = new DriveCounters(
            Reads:      new PerformanceCounter(Category, "Disk Reads/sec",       instance, readOnly: true),
            Writes:     new PerformanceCounter(Category, "Disk Writes/sec",      instance, readOnly: true),
            ReadBytes:  new PerformanceCounter(Category, "Disk Read Bytes/sec",  instance, readOnly: true),
            WriteBytes: new PerformanceCounter(Category, "Disk Write Bytes/sec", instance, readOnly: true));

        // Prime the counters - the first NextValue() of any rate counter is always 0.
        _ = c.Reads.NextValue();
        _ = c.Writes.NextValue();
        _ = c.ReadBytes.NextValue();
        _ = c.WriteBytes.NextValue();

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
        foreach (var c in _counters.Values)
        {
            c.Reads.Dispose();
            c.Writes.Dispose();
            c.ReadBytes.Dispose();
            c.WriteBytes.Dispose();
        }
        _counters.Clear();
    }

    private sealed record DriveCounters(
        PerformanceCounter Reads,
        PerformanceCounter Writes,
        PerformanceCounter ReadBytes,
        PerformanceCounter WriteBytes);
}
