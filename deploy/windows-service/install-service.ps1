#requires -version 5
<#
.SYNOPSIS
  Install KamsoraAPM HostMonitor as a Windows Service.

.DESCRIPTION
  Registers the KamsoraAPM.HostMonitor.exe sitting next to this script as an
  auto-start Windows Service and starts it. Re-running re-installs cleanly
  (stops + removes the old service first). Must be run from an elevated
  (Administrator) PowerShell.

  Before installing, edit appsettings.json next to the .exe and set
  KamsoraApm:HostMonitor CollectorEndpoint, TenantId, and ApiKey. The service
  reads appsettings.json from the .exe's own folder.

.EXAMPLE
  # In an elevated PowerShell, from the extracted folder:
  powershell -ExecutionPolicy Bypass -File install-service.ps1
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "KamsoraAPM.HostMonitor",
    [string]$DisplayName = "KamsoraAPM HostMonitor"
)

$ErrorActionPreference = "Stop"

# Must be elevated - service registration requires Administrator.
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "This must run as Administrator. Right-click PowerShell > Run as administrator, then rerun." -ForegroundColor Red
    exit 1
}

$exe = Join-Path $PSScriptRoot "KamsoraAPM.HostMonitor.exe"
if (-not (Test-Path $exe)) {
    Write-Host "KamsoraAPM.HostMonitor.exe not found next to this script ($exe)." -ForegroundColor Red
    exit 1
}

# Remove any existing install so this is idempotent.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists - stopping and removing it first..."
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Installing service '$ServiceName'..."
# Quote the binary path so spaces in the folder are handled.
New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" `
    -DisplayName $DisplayName -StartupType Automatic | Out-Null
& sc.exe description $ServiceName "KamsoraAPM host telemetry daemon (CPU/RAM/disk/network/processes)." | Out-Null

# Restart automatically if it ever crashes.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Start-Service -Name $ServiceName
$svc = Get-Service -Name $ServiceName
Write-Host ""
Write-Host "Service '$ServiceName' installed and $($svc.Status)." -ForegroundColor Green
Write-Host "Logs: Event Viewer > Windows Logs > Application, or the console sink if run interactively."
Write-Host "If metrics do not appear, confirm appsettings.json next to the .exe has the correct"
Write-Host "CollectorEndpoint / TenantId / ApiKey, then: Restart-Service $ServiceName"
