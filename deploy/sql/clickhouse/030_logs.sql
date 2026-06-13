-- KamsoraAPM telemetry store - logs.
-- One row per LogRecord. Body stored as String; severity follows OTLP
-- numeric ranges.

CREATE TABLE IF NOT EXISTS kamsora_apm.logs
(
    tenant_id            UUID,
    timestamp            DateTime64(9, 'UTC') CODEC(Delta, ZSTD(1)),
    observed_timestamp   DateTime64(9, 'UTC') CODEC(Delta, ZSTD(1)),

    service_name         LowCardinality(String),
    service_namespace    LowCardinality(String),

    severity_number      UInt8,
    severity_text        LowCardinality(String),
    body                 String CODEC(ZSTD(3)),

    -- Stored as 32-/16-char lowercase hex strings to keep parity with
    -- kamsora_apm.spans. ClickHouse.Client's bulk-copy serializer can't
    -- write byte[] into FixedString - it tries to stringify the array and
    -- UTF-8-encode the result, which overflows the fixed buffer.
    trace_id             String,
    span_id              String,

    attrs_keys           Array(String),
    attrs_values         Array(String),

    ingested_at          DateTime64(3, 'UTC') DEFAULT now64(3),
    agent_version        LowCardinality(String),

    INDEX idx_severity   severity_number TYPE minmax GRANULARITY 4,
    INDEX idx_service    service_name    TYPE bloom_filter(0.01) GRANULARITY 4,
    INDEX idx_trace      trace_id        TYPE bloom_filter(0.01) GRANULARITY 4,
    INDEX idx_body_token body TYPE tokenbf_v1(32768, 3, 0) GRANULARITY 4
)
ENGINE = MergeTree
PARTITION BY (tenant_id, toYYYYMMDD(timestamp))
ORDER BY (tenant_id, service_name, severity_number, timestamp)
TTL toDateTime(timestamp) + INTERVAL 14 DAY
SETTINGS index_granularity = 8192;
