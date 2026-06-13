// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KamsoraAPM.Contracts.Host.V1;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// Windows implementation of <see cref="ICpuMemorySampler"/>. CPU utilization
/// comes from the <c>"Processor", "% Processor Time", "_Total"</c>
/// PerformanceCounter; memory comes from <c>GlobalMemoryStatusEx</c> (Win32).
/// Load averages are not natively reported on Windows - we leave them at 0.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCpuMemorySampler : ICpuMemorySampler, IDisposable
{
    private readonly PerformanceCounter _cpuTotal;
    private readonly ILogger<WindowsCpuMemorySampler> _logger;
    private bool _disposed;

    public WindowsCpuMemorySampler(ILogger<WindowsCpuMemorySampler> logger)
    {
        _logger   = logger;
        _cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
        // First read on a Windows performance counter always returns 0 - warm
        // it up at construction so the first beat reports a meaningful value.
        _ = _cpuTotal.NextValue();
    }

    public ValueTask<(CpuSample cpu, MemorySample memory)> SampleAsync(CancellationToken cancellationToken)
    {
        var totalUtil = Math.Clamp(_cpuTotal.NextValue() / 100f, 0f, 1f);

        var cpu = new CpuSample
        {
            LogicalCores      = (uint)Environment.ProcessorCount,
            Load1M            = 0d,
            Load5M            = 0d,
            Load15M           = 0d,
            UtilizationUser   = totalUtil,
            UtilizationSystem = 0d,
            UtilizationIowait = 0d,
            UtilizationIdle   = 1d - totalUtil,
        };

        var memory = ReadMemory();

        return ValueTask.FromResult((cpu, memory));
    }

    private MemorySample ReadMemory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem))
        {
            _logger.LogWarning("KamsoraAPM HostMonitor: GlobalMemoryStatusEx failed; reporting zeros for memory.");
            return new MemorySample();
        }
        return new MemorySample
        {
            TotalBytes     = mem.ullTotalPhys,
            AvailableBytes = mem.ullAvailPhys,
            UsedBytes      = mem.ullTotalPhys > mem.ullAvailPhys ? mem.ullTotalPhys - mem.ullAvailPhys : 0,
            SwapTotalBytes = mem.ullTotalPageFile,
            SwapUsedBytes  = mem.ullTotalPageFile > mem.ullAvailPageFile ? mem.ullTotalPageFile - mem.ullAvailPageFile : 0,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cpuTotal.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
