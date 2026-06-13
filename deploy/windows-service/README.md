# KamsoraAPM HostMonitor - Windows Service

The HostMonitor is a small daemon that reports the host machine's CPU, RAM,
disk, network, and top processes to the KamsoraAPM Collector. On Windows it
runs as a Windows Service. The published download is self-contained, so the
target machine does **not** need the .NET runtime installed.

## Install

1. Download `kamsora-apm-hostmonitor-win-x64.zip` from the
   [GitHub Releases](https://github.com/kamsora/KamsoraAPM/releases) page and
   extract it (e.g. to `C:\Kamsora\HostMonitor\`).

2. Edit `appsettings.json` next to the `.exe` and set the
   `KamsoraApm:HostMonitor` values - copy them from the dashboard's
   **API Keys > Add host** wizard:

   ```json
   {
     "KamsoraApm": {
       "HostMonitor": {
         "CollectorEndpoint": "http://<collector-host>:5080",
         "TenantId":          "<tenant uuid>",
         "ApiKey":            "<ingest api key>"
       }
     }
   }
   ```

3. In an **elevated** PowerShell (Run as administrator), from the extracted
   folder:

   ```powershell
   powershell -ExecutionPolicy Bypass -File install-service.ps1
   ```

   This registers `KamsoraAPM.HostMonitor` as an auto-start service, configures
   crash-restart, and starts it. The host appears on the dashboard's **Hosts**
   page within a few seconds.

## Manage

```powershell
Restart-Service KamsoraAPM.HostMonitor   # after editing appsettings.json
Stop-Service    KamsoraAPM.HostMonitor
Get-Service     KamsoraAPM.HostMonitor
```

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File uninstall-service.ps1
```

> Note: Linux host monitoring (systemd) is on the roadmap - the Linux metric
> samplers are not implemented yet, so only Windows hosts report real metrics
> today.
