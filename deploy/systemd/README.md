# KamsoraAPM HostMonitor - Linux (systemd)

The HostMonitor is a small daemon that reports the host machine's CPU, memory,
disk, network, and top processes to the KamsoraAPM Collector. On Linux it runs
as a systemd service. The published download is self-contained, so the target
machine does **not** need the .NET runtime installed.

Metrics are read from `/proc` (`/proc/stat`, `/proc/meminfo`, `/proc/loadavg`,
`/proc/diskstats`, `/proc/net/dev`, `/proc/[pid]/stat`).

## Install

1. Download `kamsora-apm-hostmonitor-linux-x64.tar.gz` from the
   [GitHub Releases](https://github.com/kamsora/KamsoraAPM/releases) page and
   extract it:

   ```bash
   tar -xzf kamsora-apm-hostmonitor-linux-x64.tar.gz
   cd kamsora-apm-hostmonitor-linux-x64
   ```

2. Edit `appsettings.json` and set the `KamsoraApm:HostMonitor` values - copy
   them from the dashboard's **API Keys > Add host** wizard:

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

3. Install and start the service (installs to `/opt/kamsora-host-monitor`,
   enables auto-start with `Restart=always`):

   ```bash
   sudo ./install-service.sh
   ```

   The host appears on the dashboard's **Hosts** page within a few seconds.

## Manage

```bash
journalctl -u kamsora-host-monitor -f          # follow logs
sudo systemctl restart kamsora-host-monitor    # after editing appsettings.json
sudo systemctl status  kamsora-host-monitor
```

## Uninstall

```bash
sudo ./uninstall-service.sh
```

> The service runs as root by default so it can see every process in `/proc`
> for the top-N process list. To run unprivileged, set `User=` in the unit file
> (`/etc/systemd/system/kamsora-host-monitor.service`); note that process
> visibility then depends on `/proc` permissions.
