-- KamsoraAPM telemetry store - metrics.
-- One row per (tenant, metric, time, attribute-set, point). Histogram bucket
-- arrays are stored inline since most metrics emit a fixed set of buckets.

CREATE TABLE IF NOT EXISTS kamsora_apm.metric_points
(
    tenant_id            UUID,
    timestamp            DateTime64(9, 'UTC') CODEC(Delta, ZSTD(1)),
    start_timestamp      DateTime64(9, 'UTC') CODEC(Delta, ZSTD(1)),

    service_name         LowCardinality(String),
    service_namespace    LowCardinality(String),

    metric_name          LowCardinality(String),
    metric_unit          LowCardinality(String),
    metric_kind          Enum8('GAUGE' = 1, 'SUM' = 2, 'HISTOGRAM' = 3),
    aggregation_temporality Enum8('UNSPECIFIED' = 0, 'DELTA' = 1, 'CUMULATIVE' = 2),
    is_monotonic         UInt8,

    -- Scalar value (used by GAUGE / SUM)
    value_double         Nullable(Float64),
    value_int            Nullable(Int64),

    -- Histogram payload
    histogram_count      Nullable(UInt64),
    histogram_sum        Nullable(Float64),
    histogram_min        Nullable(Float64),
    histogram_max        Nullable(Float64),
    histogram_bucket_counts Array(UInt64),
    histogram_bucket_bounds Array(Float64),

    -- Attribute set (sorted by key for partition friendliness)
    attrs_keys           Array(String),
    attrs_values         Array(String),
    attrs_hash           UInt64 MATERIALIZED cityHash64(arrayConcat(attrs_keys, attrs_values)),

    ingested_at          DateTime64(3, 'UTC') DEFAULT now64(3),
    agent_version        LowCardinality(String),

    INDEX idx_metric_name metric_name TYPE bloom_filter(0.01) GRANULARITY 4,
    INDEX idx_service     service_name TYPE bloom_filter(0.01) GRANULARITY 4
)
ENGINE = MergeTree
PARTITION BY (tenant_id, toYYYYMMDD(timestamp))
ORDER BY (tenant_id, service_name, metric_name, attrs_hash, timestamp)
TTL toDateTime(timestamp) + INTERVAL 90 DAY
SETTINGS index_granularity = 8192;
