#!/usr/bin/env bash
#
# Install KamsoraAPM HostMonitor as a systemd service.
#
# Extract the release tarball, edit appsettings.json (CollectorEndpoint,
# TenantId, ApiKey), then from the extracted folder:
#   sudo ./install-service.sh
#
# Re-running upgrades the binary in place and preserves your appsettings.json.
set -eu

INSTALL_DIR=/opt/kamsora-host-monitor
UNIT_DST=/etc/systemd/system/kamsora-host-monitor.service
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root: sudo ./install-service.sh" >&2
  exit 1
fi

mkdir -p "$INSTALL_DIR"
install -m 0755 "$SCRIPT_DIR/KamsoraAPM.HostMonitor" "$INSTALL_DIR/KamsoraAPM.HostMonitor"

if [ -f "$INSTALL_DIR/appsettings.json" ]; then
  echo "Keeping existing $INSTALL_DIR/appsettings.json"
else
  install -m 0644 "$SCRIPT_DIR/appsettings.json" "$INSTALL_DIR/appsettings.json"
fi

install -m 0644 "$SCRIPT_DIR/kamsora-host-monitor.service" "$UNIT_DST"

systemctl daemon-reload
systemctl enable --now kamsora-host-monitor
echo ""
systemctl --no-pager --full status kamsora-host-monitor || true
echo ""
echo "Installed. Follow logs with:  journalctl -u kamsora-host-monitor -f"
echo "If it is not healthy, check CollectorEndpoint/TenantId/ApiKey in"
echo "$INSTALL_DIR/appsettings.json then:  sudo systemctl restart kamsora-host-monitor"
