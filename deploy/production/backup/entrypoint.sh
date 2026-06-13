#!/bin/sh
# Wires the BACKUP_CRON_SCHEDULE env var into crond and starts it in the foreground.
set -eu

SCHED="${BACKUP_CRON_SCHEDULE:-0 3 * * *}"

# Pass env into cron's environment (cron strips most vars otherwise).
# We export to a sourced env file consumed by backup.sh.
cat > /etc/backup.env <<EOF
PGHOST=${PGHOST}
PGUSER=${PGUSER}
PGPASSWORD=${PGPASSWORD}
PGDATABASE=${PGDATABASE}
CLICKHOUSE_HOST=${CLICKHOUSE_HOST}
CLICKHOUSE_USER=${CLICKHOUSE_USER}
CLICKHOUSE_PASSWORD=${CLICKHOUSE_PASSWORD}
BACKUP_RETENTION_DAYS=${BACKUP_RETENTION_DAYS}
EOF
chmod 600 /etc/backup.env

# Write crontab. /var/log/backup.log isn't a real file — we redirect to stdout.
echo "$SCHED . /etc/backup.env; /usr/local/bin/backup.sh >> /proc/1/fd/1 2>&1" > /etc/crontabs/root

echo "[backup] Schedule installed: $SCHED"
echo "[backup] Running first backup now (so we know the credentials work)..."
# shellcheck disable=SC1091
. /etc/backup.env
/usr/local/bin/backup.sh || echo "[backup] WARNING: first run failed; cron will retry on schedule."

echo "[backup] Starting cron daemon (foreground)..."
exec crond -f -l 2
