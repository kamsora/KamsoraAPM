-- KamsoraAPM M8 - logs + metrics rollups.
--
-- The kamsora_apm.logs and kamsora_apm.metric_points tables already exist
-- from the M1 schema. M8 adds two AggregatingMergeTree rollups so the
-- Logs + Metrics dashboards can render minute-grained volume / value
-- charts without scanning raw log rows.
--
-- Idempotent: safe to re-run.

-- ---------------------------------------------------------------------------
-- logs_minutely_rollup
-- Per-(tenant, service, severity_text, minute) log volume. Drives the
-- "volume over time + severity stack" chart on the Logs page.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS kamsora_apm.logs_minutely_rollup
(
    tenant_id        UUID,
    bucket_minute    DateTime CODEC(Delta, ZSTD(1)),
    service_name     LowCardinality(String),
    severity_text    LowCardinality(String),
    log_count        AggregateFunction(sum, UInt64)
)
ENGINE = AggregatingMergeTree
PARTITION BY (tenant_id, toYYYYMM(bucket_minute))
ORDER BY (tenant_id, service_name, severity_text, bucket_minute)
TTL bucket_minute + INTERVAL 60 DAY;

CREATE MATERIALIZED VIEW IF NOT EXISTS kamsora_apm.mv_logs_to_minutely_rollup
TO kamsora_apm.logs_minutely_rollup AS
SELECT
    tenant_id,
    toStartOfMinute(timestamp)  AS bucket_minute,
    service_name,
    severity_text,
    sumState(toUInt64(1))       AS log_count
FROM kamsora_apm.logs
GROUP BY tenant_id, bucket_minute, service_name, severity_text;

-- ---------------------------------------------------------------------------
-- metrics_minutely_rollup
-- Per-(tenant, service, metric_name, attrs_hash, minute) scalar value rollup.
-- For GAUGE we take the latest value of the bucket; for SUM we take the
-- max (assumes cumulative temporality - DELTA mode handled at the reader by
-- summing per bucket directly off the raw table). Histograms keep their
-- per-point row in the raw table - the rollup is intentionally for the
-- common scalar case.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS kamsora_apm.metrics_minutely_rollup
(
    tenant_id           UUID,
    bucket_minute       DateTime CODEC(Delta, ZSTD(1)),
    service_name        LowCardinality(String),
    metric_name         LowCardinality(String),
    metric_kind         Enum8('GAUGE' = 1, 'SUM' = 2, 'HISTOGRAM' = 3),
    attrs_hash          UInt64,
    attrs_keys          Array(String),
    attrs_values        Array(String),
    value_last          AggregateFunction(anyLast, Float64),
    value_max           AggregateFunction(max, Float64),
    value_min           AggregateFunction(min, Float64),
    point_count         AggregateFunction(sum, UInt64)
)
ENGINE = AggregatingMergeTree
PARTITION BY (tenant_id, toYYYYMM(bucket_minute))
ORDER BY (tenant_id, service_name, metric_name, attrs_hash, bucket_minute)
TTL bucket_minute + INTERVAL 180 DAY;

CREATE MATERIALIZED VIEW IF NOT EXISTS kamsora_apm.mv_metric_points_to_minutely_rollup
TO kamsora_apm.metrics_minutely_rollup AS
SELECT
    tenant_id,
    toStartOfMinute(timestamp)                                          AS bucket_minute,
    service_name,
    metric_name,
    metric_kind,
    attrs_hash,
    attrs_keys,
    attrs_values,
    anyLastState(coalesce(value_double, toFloat64(coalesce(value_int, 0)))) AS value_last,
    maxState(coalesce(value_double, toFloat64(coalesce(value_int, 0))))     AS value_max,
    minState(coalesce(value_double, toFloat64(coalesce(value_int, 0))))     AS value_min,
    sumState(toUInt64(1))                                               AS point_count
FROM kamsora_apm.metric_points
WHERE metric_kind != 'HISTOGRAM'
GROUP BY tenant_id, bucket_minute, service_name, metric_name, metric_kind, attrs_hash, attrs_keys, attrs_values;
