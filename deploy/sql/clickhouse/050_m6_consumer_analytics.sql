-- KamsoraAPM M6.1 - consumer analytics.
--
-- Adds:
--   * spans.consumer_id            - LowCardinality(String), extracted by the Agent
--     from a JWT claim / header / client IP (configurable per service).
--   * consumer_hourly_rollup       - AggregatingMergeTree, one row per
--     (tenant, consumer, route, hour). Powers the Consumers list + drill-down.
--   * mv_spans_to_consumer_hourly  - materialised view that streams new spans
--     into the rollup as they arrive (server-kind only - keeps the rollup small).
--
-- Idempotent: safe to re-run.

-- 1. Add consumer_id to the span table. Default '' so back-fill is implicit
--    and existing rows query without nulls.
ALTER TABLE kamsora_apm.spans
    ADD COLUMN IF NOT EXISTS consumer_id LowCardinality(String) DEFAULT '' AFTER http_client_ip;

-- Skip-index for "list spans by consumer" lookups; bloom_filter handles the
-- high-cardinality case (anon = clientIp) without exploding the index size.
ALTER TABLE kamsora_apm.spans
    ADD INDEX IF NOT EXISTS idx_consumer_id consumer_id TYPE bloom_filter(0.01) GRANULARITY 4;

-- 2. Per-(tenant, consumer, route, hour) rollup. We keep route in the key so the
--    "top routes for a consumer" + "top consumers for a route" queries are both
--    O(rollup-rows-for-the-bucket) instead of scanning raw spans.
--
-- Why hourly (not minutely)?
--   * Consumer dashboards show last 24h / 7d by default; hourly resolution is
--     enough and keeps the rollup ~60x smaller than per-minute.
--   * The minutely_rollup already covers fine-grained service-level drilldown.
CREATE TABLE IF NOT EXISTS kamsora_apm.consumer_hourly_rollup
(
    tenant_id           UUID,
    bucket_hour         DateTime CODEC(Delta, ZSTD(1)),
    consumer_id         LowCardinality(String),
    service_name        LowCardinality(String),
    http_route          LowCardinality(String),
    request_count       AggregateFunction(sum, UInt64),
    error_count         AggregateFunction(sum, UInt64),
    client_error_count  AggregateFunction(sum, UInt64),   -- 4xx
    server_error_count  AggregateFunction(sum, UInt64),   -- 5xx
    latency_quantiles   AggregateFunction(quantilesTDigest(0.5, 0.9, 0.99), UInt64),
    bytes_in            AggregateFunction(sum, UInt64),   -- placeholder, populated when proto carries it
    bytes_out           AggregateFunction(sum, UInt64)
)
ENGINE = AggregatingMergeTree
PARTITION BY (tenant_id, toYYYYMM(bucket_hour))
ORDER BY (tenant_id, consumer_id, service_name, http_route, bucket_hour)
TTL bucket_hour + INTERVAL 180 DAY;

CREATE MATERIALIZED VIEW IF NOT EXISTS kamsora_apm.mv_spans_to_consumer_hourly
TO kamsora_apm.consumer_hourly_rollup AS
SELECT
    tenant_id,
    toStartOfHour(timestamp)                                          AS bucket_hour,
    consumer_id,
    service_name,
    http_route,
    sumState(toUInt64(1))                                             AS request_count,
    sumState(toUInt64(status_code = 'ERROR'))                         AS error_count,
    sumState(toUInt64(http_status_code BETWEEN 400 AND 499))          AS client_error_count,
    sumState(toUInt64(http_status_code >= 500))                       AS server_error_count,
    quantilesTDigestState(0.5, 0.9, 0.99)(duration_ns)                AS latency_quantiles,
    sumState(toUInt64(0))                                             AS bytes_in,
    sumState(toUInt64(0))                                             AS bytes_out
FROM kamsora_apm.spans
WHERE span_kind = 'SERVER'    -- inbound API requests only
GROUP BY tenant_id, bucket_hour, consumer_id, service_name, http_route;

-- 3. Status-code rollup - powers the Errors page (4xx/5xx breakdown by exact
--    HTTP status). Smaller key than the consumer rollup; can scan it freely.
CREATE TABLE IF NOT EXISTS kamsora_apm.status_hourly_rollup
(
    tenant_id           UUID,
    bucket_hour         DateTime CODEC(Delta, ZSTD(1)),
    service_name        LowCardinality(String),
    http_route          LowCardinality(String),
    http_status_code    UInt16,
    request_count       AggregateFunction(sum, UInt64),
    latency_quantiles   AggregateFunction(quantilesTDigest(0.5, 0.9, 0.99), UInt64)
)
ENGINE = AggregatingMergeTree
PARTITION BY (tenant_id, toYYYYMM(bucket_hour))
ORDER BY (tenant_id, service_name, http_route, http_status_code, bucket_hour)
TTL bucket_hour + INTERVAL 180 DAY;

CREATE MATERIALIZED VIEW IF NOT EXISTS kamsora_apm.mv_spans_to_status_hourly
TO kamsora_apm.status_hourly_rollup AS
SELECT
    tenant_id,
    toStartOfHour(timestamp)                              AS bucket_hour,
    service_name,
    http_route,
    http_status_code,
    sumState(toUInt64(1))                                 AS request_count,
    quantilesTDigestState(0.5, 0.9, 0.99)(duration_ns)    AS latency_quantiles
FROM kamsora_apm.spans
WHERE span_kind = 'SERVER' AND http_status_code > 0
GROUP BY tenant_id, bucket_hour, service_name, http_route, http_status_code;
