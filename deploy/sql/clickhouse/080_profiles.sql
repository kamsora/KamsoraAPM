-- KamsoraAPM M9 — continuous profiling.
-- One row per profile capture. The pprof payload (perftools.profiles.Profile)
-- is stored Base64-encoded inside `pprof_bytes` to keep the column type
-- `String` — ClickHouse.Client's bulk-copy serializer encodes strings as
-- UTF-8 and does not have a stable path for arbitrary byte[] into the
-- `String` column on every release. Base64 sidesteps that with a fixed
-- ~33% overhead which ZSTD(9) reclaims almost completely (pprof is already
-- protobuf-compact; the Base64 expansion is low-entropy text).

CREATE TABLE IF NOT EXISTS kamsora_apm.profiles
(
    tenant_id            UUID,
    start_timestamp      DateTime64(9, 'UTC') CODEC(Delta, ZSTD(1)),
    duration_nanos       UInt64,

    service_name         LowCardinality(String),
    service_namespace    LowCardinality(String),

    profile_kind         LowCardinality(String),     -- CPU | WALL | ALLOC | LOCK | GC
    sample_count         UInt64,

    -- Base64-encoded perftools.profiles.Profile bytes.
    pprof_bytes          String CODEC(ZSTD(9)),

    -- 32-char lowercase hex trace id when the capture was triggered for one
    -- specific request (M9.x). Empty string for the periodic captures
    -- shipped in M9 v1.
    trigger_trace_id     String,

    attrs_keys           Array(String),
    attrs_values         Array(String),

    ingested_at          DateTime64(3, 'UTC') DEFAULT now64(3),
    agent_version        LowCardinality(String),

    INDEX idx_kind       profile_kind   TYPE minmax            GRANULARITY 4,
    INDEX idx_service    service_name   TYPE bloom_filter(0.01) GRANULARITY 4,
    INDEX idx_trigger    trigger_trace_id TYPE bloom_filter(0.01) GRANULARITY 4
)
ENGINE = MergeTree
PARTITION BY (tenant_id, toYYYYMMDD(start_timestamp))
ORDER BY (tenant_id, service_name, profile_kind, start_timestamp)
TTL toDateTime(start_timestamp) + INTERVAL 14 DAY
SETTINGS index_granularity = 8192;
