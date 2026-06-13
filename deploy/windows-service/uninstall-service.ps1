#requires -version 5
<#
.SYNOPSIS
  Stop and remove the KamsoraAPM HostMonitor Windows Service.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File uninstall-service.ps1
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "KamsoraAPM.HostMonitor"
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "This must run as Administrator. Right-click PowerShell > Run as administrator, then rerun." -ForegroundColor Red
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' is not installed - nothing to do."
    exit 0
}

if ($existing.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
}
& sc.exe delete $ServiceName | Out-Null
Write-Host "Service '$ServiceName' removed." -ForegroundColor Green
