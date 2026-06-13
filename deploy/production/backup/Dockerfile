# Daily backup sidecar - pg_dump for Postgres, native ClickHouse SQL backup over HTTP.
# Logs to stdout (visible via `docker compose logs backup`).
#
# Backups are written to /backups (mounted as a named volume) so the host can
# rsync them off-box, or a cloud bucket sidecar can push to S3/GCS.

FROM alpine:3.20

# pg_dump for PG 16, curl for ClickHouse REST, gzip for compression, dcron for scheduling.
RUN apk add --no-cache \
        postgresql16-client \
        curl \
        gzip \
        tzdata \
        dcron \
        tini

COPY backup.sh /usr/local/bin/backup.sh
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/backup.sh /usr/local/bin/entrypoint.sh

ENV TZ=UTC
WORKDIR /backups

# tini is the init so cron daemons forward SIGTERM correctly.
ENTRYPOINT ["/sbin/tini", "--", "/usr/local/bin/entrypoint.sh"]
