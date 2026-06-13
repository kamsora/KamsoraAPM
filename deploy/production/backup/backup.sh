#!/bin/sh
# Daily backup of Postgres + ClickHouse to /backups.
# Retention: BACKUP_RETENTION_DAYS days (default 7).
#
# Output layout:
#   /backups/postgres/YYYY-MM-DD/kamsora_apm.sql.gz
#   /backups/clickhouse/YYYY-MM-DD/kamsora_apm-{table}.tsv.gz
#
# Postgres: full pg_dump, gzip-compressed.
# ClickHouse: per-table TabSeparated dumps; restore by re-importing into the
# same DDL. For very large CH installs swap this for the clickhouse-backup tool.

set -eu

DATE="$(date -u +%Y-%m-%d)"
RET="${BACKUP_RETENTION_DAYS:-7}"

PG_OUT="/backups/postgres/${DATE}"
CH_OUT="/backups/clickhouse/${DATE}"
mkdir -p "$PG_OUT" "$CH_OUT"

log()  { echo "[backup $(date -u +%H:%M:%S)] $*"; }

# ---------- Postgres ----------
log "Postgres dump → $PG_OUT/kamsora_apm.sql.gz"
PGPASSWORD="$PGPASSWORD" pg_dump \
    -h "$PGHOST" -U "$PGUSER" -d "$PGDATABASE" \
    --no-owner --no-privileges \
    | gzip -9 > "$PG_OUT/kamsora_apm.sql.gz"
PG_SIZE=$(du -h "$PG_OUT/kamsora_apm.sql.gz" | cut -f1)
log "Postgres OK ($PG_SIZE)"

# ---------- ClickHouse ----------
# Enumerate tables in kamsora_apm and dump each as TSV.
TABLES=$(curl -fsS \
    --user "${CLICKHOUSE_USER}:${CLICKHOUSE_PASSWORD}" \
    "http://${CLICKHOUSE_HOST}:8123/?database=kamsora_apm" \
    --data "SHOW TABLES FROM kamsora_apm FORMAT TabSeparated")

for tbl in $TABLES; do
    # Skip materialized views — they regenerate from base tables.
    case "$tbl" in
        mv_*|.inner*) log "ClickHouse skip materialized: $tbl"; continue ;;
    esac
    log "ClickHouse dump → $CH_OUT/kamsora_apm-${tbl}.tsv.gz"
    curl -fsS \
        --user "${CLICKHOUSE_USER}:${CLICKHOUSE_PASSWORD}" \
        "http://${CLICKHOUSE_HOST}:8123/?database=kamsora_apm" \
        --data "SELECT * FROM kamsora_apm.${tbl} FORMAT TabSeparatedWithNames" \
        | gzip -9 > "$CH_OUT/kamsora_apm-${tbl}.tsv.gz"
done
log "ClickHouse OK"

# ---------- Retention ----------
log "Pruning backups older than ${RET} day(s)..."
find /backups/postgres   -mindepth 1 -maxdepth 1 -type d -mtime "+${RET}" -exec rm -rf {} +
find /backups/clickhouse -mindepth 1 -maxdepth 1 -type d -mtime "+${RET}" -exec rm -rf {} +

log "Done."
