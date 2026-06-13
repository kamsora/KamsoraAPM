-- KamsoraAPM telemetry store - host snapshots.
-- One row per (tenant, host, time, sub-resource). Three tables so each
-- sub-resource has its own columnar layout instead of nullable bloat.

CREATE TABLE IF NOT EXISTS kamsora_apm.host_cpu_memory
(
    tenant_id              UUID,
    timestamp              DateTime64(9, 'UTC') CODEC(Delta, ZSTD(1)),
    host_id                LowCardinality(String),
    host_name              LowCardinality(String),
    os_type                LowCardinality(String),
    os_version             LowCardinality(String),

    logical_cores          UInt16,
    load_1m                Float32,
    load_5m                Float32,
    load_15m               Float32,
    cpu_util_user          Float32,
    cpu_util_system        Float32,
    cpu_util_iowait        Float32,
    cpu_util_idle          Float32,

    mem_total_bytes        UInt64,
    mem_available_bytes    UInt64,
    mem_used_bytes         UInt64,
    swap_total_bytes       UInt64,
    swap_used_bytes        UInt64
)
ENGINE = MergeTree
PARTITION BY (tenant_id, toYYYYMMDD(timestamp))
ORDER BY (tenant_id, host_id, timestamp)
TTL toDateTime(timestamp) + INTERVAL 30 DAY
SETTINGS index_granularity = 8192;

CREATE TABLE IF NOT EXISTS kamsora_apm.host_disks
(
    tenant_id              UUID,
    timestamp              DateTime64(9, 'UTC') CODEC(Delta, ZSTD(1)),
    host_id                LowCardinality(String),
    device                 LowCardinality(String),
    mountpoint             LowCardinality(String),
    total_bytes            UInt64,
    used_bytes             UInt64,
    reads_per_sec          UInt64,
    writes_per_sec         UInt64,
    read_bytes_per_sec     UInt64,
    write_bytes_per_sec    UInt64
)
ENGINE = MergeTree
PARTITION BY (tenant_id, toYYYYMMDD(timestamp))
ORDER BY (tenant_id, host_id, device, timestamp)
TTL toDateTime(timestamp) + INTERVAL 30 DAY;

CREATE TABLE IF NOT EXISTS kamsora_apm.host_networks
(
    tenant_id              UUID,
    timestamp              DateTime64(9, 'UTC') CODEC(Delta, ZSTD(1)),
    host_id                LowCardinality(String),
    interface_name         LowCardinality(String),
    rx_bytes_per_sec       UInt64,
    tx_bytes_per_sec       UInt64,
    rx_packets_per_sec     UInt64,
    tx_packets_per_sec     UInt64,
    rx_errors              UInt64,
    tx_errors              UInt64
)
ENGINE = MergeTree
PARTITION BY (tenant_id, toYYYYMMDD(timestamp))
ORDER BY (tenant_id, host_id, interface_name, timestamp)
TTL toDateTime(timestamp) + INTERVAL 30 DAY;

CREATE TABLE IF NOT EXISTS kamsora_apm.host_processes
(
    tenant_id              UUID,
    timestamp              DateTime64(9, 'UTC') CODEC(Delta, ZSTD(1)),
    host_id                LowCardinality(String),
    pid                    UInt32,
    command                String CODEC(ZSTD(3)),
    user_name              LowCardinality(String),
    runtime_version        LowCardinality(String),
    service_name           LowCardinality(String),
    cpu_utilization        Float32,
    rss_bytes              UInt64,
    thread_count           UInt32,
    handle_count           UInt32
)
ENGINE = MergeTree
PARTITION BY (tenant_id, toYYYYMMDD(timestamp))
ORDER BY (tenant_id, host_id, service_name, pid, timestamp)
TTL toDateTime(timestamp) + INTERVAL 7 DAY; -- per-PID retention is shorter
