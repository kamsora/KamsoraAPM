-- KamsoraAPM telemetry store - distributed-tracing spans.
--
-- Design notes:
--   * One row per Span. trace_id / span_id stored as FixedString(16) / (8)
--     for query efficiency (compatible with OTLP byte ids).
--   * Tenant isolation via tenant_id in the primary key and partition key.
--     Cross-tenant queries are not allowed; the Dashboard.Api always scopes
--     queries by tenant_id and the row policy below enforces it.
--   * Codec choice: ZSTD(3) is a good default for span attributes which
--     are highly compressible.
--   * TTL is set per-tenant via the dashboard; default 14 days driven by
--     the alter_ttl maintenance job. Here we set a baseline 30-day cap so
--     forgotten deployments don't grow unbounded.

CREATE TABLE IF NOT EXISTS kamsora_apm.spans
(
    tenant_id           UUID,
    timestamp           DateTime64(9, 'UTC') CODEC(Delta, ZSTD(1)),
    trace_id            String,   -- lowercase hex (32 chars). Was FixedString(16).
    span_id             String,   -- lowercase hex (16 chars).
    parent_span_id      String,
    trace_state         LowCardinality(String),

    service_name        LowCardinality(String),
    service_namespace   LowCardinality(String),
    service_version     LowCardinality(String),
    span_name           LowCardinality(String),
    span_kind           Enum8('INTERNAL' = 1, 'SERVER' = 2, 'CLIENT' = 3,
                              'PRODUCER' = 4, 'CONSUMER' = 5),

    start_time_unix_ns  UInt64 CODEC(Delta, ZSTD(1)),
    end_time_unix_ns    UInt64 CODEC(Delta, ZSTD(1)),
    duration_ns         UInt64 MATERIALIZED end_time_unix_ns - start_time_unix_ns,

    status_code         Enum8('UNSET' = 0, 'OK' = 1, 'ERROR' = 2),
    status_message      String CODEC(ZSTD(3)),

    -- Common HTTP-server attributes hoisted out of attrs_map for fast querying.
    http_method         LowCardinality(String),
    http_status_code    UInt16,
    http_route          LowCardinality(String),
    http_url            String CODEC(ZSTD(3)),
    http_client_ip      String,

    -- DB attributes hoisted out for the "DB time per request" view.
    db_system           LowCardinality(String),
    db_statement        String CODEC(ZSTD(3)),
    db_duration_ns      UInt64,

    -- Remaining attributes as a String map. Keep keys short.
    attrs_keys          Array(String),
    attrs_values        Array(String),

    -- Events / exceptions captured during the span.
    event_names         Array(String),
    event_times_unix_ns Array(UInt64),
    event_attrs_json    Array(String) CODEC(ZSTD(3)),

    -- Bookkeeping
    ingested_at         DateTime64(3, 'UTC') DEFAULT now64(3),
    agent_version       LowCardinality(String),

    INDEX idx_trace_id  trace_id   TYPE bloom_filter(0.01) GRANULARITY 4,
    INDEX idx_service   service_name TYPE bloom_filter(0.01) GRANULARITY 4,
    INDEX idx_http_route http_route TYPE bloom_filter(0.01) GRANULARITY 4
)
ENGINE = MergeTree
PARTITION BY (tenant_id, toYYYYMMDD(timestamp))
ORDER BY (tenant_id, service_name, span_kind, timestamp, trace_id)
TTL toDateTime(timestamp) + INTERVAL 30 DAY
SETTINGS index_granularity = 8192;

-- Materialised view: rollup-per-minute per (tenant, service, span_kind)
-- powers the Unified Health Overview latency chart without scanning raw spans.
CREATE TABLE IF NOT EXISTS kamsora_apm.spans_minutely_rollup
(
    tenant_id           UUID,
    bucket_minute       DateTime CODEC(Delta, ZSTD(1)),
    service_name        LowCardinality(String),
    span_kind           Enum8('INTERNAL' = 1, 'SERVER' = 2, 'CLIENT' = 3,
                              'PRODUCER' = 4, 'CONSUMER' = 5),
    request_count       AggregateFunction(sum, UInt64),
    error_count         AggregateFunction(sum, UInt64),
    latency_quantiles   AggregateFunction(quantilesTDigest(0.5, 0.9, 0.99), UInt64)
)
ENGINE = AggregatingMergeTree
PARTITION BY (tenant_id, toYYYYMM(bucket_minute))
ORDER BY (tenant_id, service_name, span_kind, bucket_minute)
TTL bucket_minute + INTERVAL 90 DAY;

CREATE MATERIALIZED VIEW IF NOT EXISTS kamsora_apm.mv_spans_to_minutely_rollup
TO kamsora_apm.spans_minutely_rollup AS
SELECT
    tenant_id,
    toStartOfMinute(timestamp)                                       AS bucket_minute,
    service_name,
    span_kind,
    sumState(toUInt64(1))                                            AS request_count,
    sumState(toUInt64(status_code = 'ERROR'))                        AS error_count,
    quantilesTDigestState(0.5, 0.9, 0.99)(duration_ns)               AS latency_quantiles
FROM kamsora_apm.spans
GROUP BY tenant_id, bucket_minute, service_name, span_kind;
