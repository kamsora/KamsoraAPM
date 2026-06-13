#!/usr/bin/env bash
#
# Stop and remove the KamsoraAPM HostMonitor systemd service.
#   sudo ./uninstall-service.sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root: sudo ./uninstall-service.sh" >&2
  exit 1
fi

systemctl disable --now kamsora-host-monitor 2>/dev/null || true
rm -f /etc/systemd/system/kamsora-host-monitor.service
systemctl daemon-reload
echo "Service removed. /opt/kamsora-host-monitor was left in place; delete it manually if you no longer need it."
