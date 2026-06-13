// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KamsoraAPM.HostMonitor.Sampling;

/// <summary>
/// Computes a stable identifier for the host. Order of preference:
///   1. operator-supplied override from configuration,
///   2. OS machine GUID (Windows registry HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid,
///      Linux /etc/machine-id),
///   3. SHA-256 of MachineName as last-resort fallback.
///
/// The identifier is used as the partition / order-key for host telemetry, so
/// it must NEVER change across daemon restarts on the same machine.
/// </summary>
internal static class HostIdentity
{
    public static string Resolve(string? overrideId)
    {
        if (!string.IsNullOrWhiteSpace(overrideId))
            return overrideId.Trim();

        if (OperatingSystem.IsWindows())
        {
            var win = TryReadWindowsMachineGuid();
            if (!string.IsNullOrWhiteSpace(win)) return win!;
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                if (File.Exists("/etc/machine-id"))
                {
                    var raw = File.ReadAllText("/etc/machine-id").Trim();
                    if (!string.IsNullOrWhiteSpace(raw)) return raw;
                }
            }
            catch
            {
                // Fall through to MachineName fallback.
            }
        }

        // Last-resort: hash of MachineName. Stable on the same box, distinct
        // across boxes with different hostnames.
        var bytes = System.Text.Encoding.UTF8.GetBytes(Environment.MachineName);
        var hash  = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    public static string GetOsType() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsLinux()   ? "linux"   :
        OperatingSystem.IsMacOS()   ? "macos"   :
        RuntimeInformation.OSDescription;

    public static string GetOsVersion() => Environment.OSVersion.VersionString;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? TryReadWindowsMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }
}
