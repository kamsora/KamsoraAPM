// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Host.V1;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// Linux <see cref="IDiskSampler"/>. One sample per real mounted filesystem
/// (from <c>/proc/mounts</c>, filtered to on-disk fs types). Capacity comes from
/// <see cref="DriveInfo"/> (statvfs under the hood); read/write throughput from
/// the backing block device's row in <c>/proc/diskstats</c>, delta'd to
/// per-second. Sectors are the kernel's fixed 512-byte units.
/// </summary>
public sealed class LinuxDiskSampler : IDiskSampler
{
    private const ulong SectorBytes = 512UL;

    private static readonly HashSet<string> RealFsTypes = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "reiserfs", "jfs", "vfat", "exfat", "ntfs",
    };

    private readonly ILogger<LinuxDiskSampler> _logger;
    private readonly Dictionary<string, DiskCounters> _previous = new(StringComparer.Ordinal);
    private DateTime _lastUtc = DateTime.UtcNow;

    public LinuxDiskSampler(ILogger<LinuxDiskSampler> logger) => _logger = logger;

    public ValueTask<IReadOnlyList<DiskSample>> SampleAsync(CancellationToken cancellationToken)
    {
        var results   = new List<DiskSample>(4);
        var nowUtc    = DateTime.UtcNow;
        var elapsed   = Math.Max(0.001, (nowUtc - _lastUtc).TotalSeconds);
        var diskstats = ReadDiskStats();

        foreach (var (device, mountpoint) in ReadRealMounts())
        {
            var sample = new DiskSample { Device = device, Mountpoint = mountpoint };

            try
            {
                var di = new DriveInfo(mountpoint);
                if (di.IsReady && di.TotalSize > 0)
                {
                    var total = (ulong)di.TotalSize;
                    var free  = (ulong)di.TotalFreeSpace;
                    sample.TotalBytes = total;
                    sample.UsedBytes  = total > free ? total - free : 0;
                }
            }
            catch
            {
                // Mount vanished between reading /proc/mounts and querying it, or not accessible.
            }

            // Skip zero-capacity entries: unreadable mounts, and the single-file
            // bind mounts (e.g. /etc/resolv.conf) that containers expose as block devices.
            if (sample.TotalBytes == 0) continue;

            // I/O rates from the backing device row in /proc/diskstats, when matched.
            var devName = device.StartsWith("/dev/", StringComparison.Ordinal) ? device[5..] : device;
            if (diskstats.TryGetValue(devName, out var cur))
            {
                if (_previous.TryGetValue(devName, out var prev))
                {
                    sample.ReadsPerSec      = Rate(cur.Reads,        prev.Reads,        elapsed);
                    sample.WritesPerSec     = Rate(cur.Writes,       prev.Writes,       elapsed);
                    sample.ReadBytesPerSec  = Rate(cur.SectorsRead,  prev.SectorsRead,  elapsed) * SectorBytes;
                    sample.WriteBytesPerSec = Rate(cur.SectorsWrite, prev.SectorsWrite, elapsed) * SectorBytes;
                }
                _previous[devName] = cur;
            }

            results.Add(sample);
        }

        _lastUtc = nowUtc;
        return ValueTask.FromResult<IReadOnlyList<DiskSample>>(results);
    }

    private IEnumerable<(string device, string mountpoint)> ReadRealMounts()
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines("/proc/mounts");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM HostMonitor: failed to read /proc/mounts.");
            lines = Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) continue;

            var device = p[0];
            var fstype = p[2];
            if (!RealFsTypes.Contains(fstype)) continue;
            if (!device.StartsWith("/dev/", StringComparison.Ordinal)) continue;
            if (!seen.Add(device)) continue; // collapse bind mounts of the same device

            yield return (device, Unescape(p[1]));
        }
    }

    // /proc/mounts octal-escapes spaces (\040) and tabs (\011) in the mountpoint.
    private static string Unescape(string s)
        => s.Contains('\\', StringComparison.Ordinal)
            ? s.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal)
            : s;

    private Dictionary<string, DiskCounters> ReadDiskStats()
    {
        var map = new Dictionary<string, DiskCounters>(StringComparer.Ordinal);
        try
        {
            // Columns: major minor name reads(3) rd_merged(4) sectors_read(5) ms_read(6)
            //          writes(7) wr_merged(8) sectors_written(9) ...
            foreach (var line in File.ReadLines("/proc/diskstats"))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 10) continue;
                ulong U(int i) => ulong.TryParse(p[i], out var v) ? v : 0UL;
                map[p[2]] = new DiskCounters(U(3), U(7), U(5), U(9));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KamsoraAPM HostMonitor: failed to read /proc/diskstats.");
        }
        return map;
    }

    private static ulong Rate(ulong cur, ulong prev, double elapsed)
        => cur > prev ? (ulong)((cur - prev) / elapsed) : 0UL;

    private readonly record struct DiskCounters(ulong Reads, ulong Writes, ulong SectorsRead, ulong SectorsWrite);
}
