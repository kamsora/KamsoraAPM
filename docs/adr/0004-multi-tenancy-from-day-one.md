# ADR-0004: Multi-tenancy from day one

- **Status**: Accepted
- **Date**: 2026-05-24
- **Component(s)**: `KamsoraAPM.Storage`, `KamsoraAPM.Collector`, `KamsoraAPM.Dashboard.Api`, schemas

## Context

KamsoraAPM is positioned for both self-hosted single-organisation
deployments and a potential hosted multi-tenant SaaS. We must choose
whether to encode multi-tenancy from the start or to retrofit it later.

Three options:

1. **Single-tenant forever.** Like Prometheus/Grafana OSS — one instance
   per organisation. Simplest codebase, forecloses any hosted offering.
2. **Single-tenant MVP, multi-tenant later.** Defer tenant isolation to a
   future milestone. Faster MVP, but retrofitting partition keys across
   ClickHouse and Postgres later is an enormous breaking change.
3. **Multi-tenant from M0.** Every storage table partitioned by tenant;
   every API authenticated as a tenant; row-level isolation enforced from
   the first migration. More upfront work, no breaking change later.

Retrofitting tenant_id into a partition key in ClickHouse requires
re-ingestion (you cannot ALTER a MergeTree's ORDER BY without dropping the
table). Retrofitting tenant_id into PostgreSQL is also painful because
every join and foreign key must be rewritten. The cost of getting this
wrong grows superlinearly with the amount of data accumulated.

## Decision

KamsoraAPM is **multi-tenant from M0**. Specifically:

- All time-series tables in ClickHouse include `tenant_id UUID` as the
  **first column** of both `PARTITION BY` and `ORDER BY`. Cross-tenant
  reads are not possible at the storage layer.
- All relational tables in PostgreSQL include a `systenantuuid text`
  column (per the Kamsora PG pattern) referencing `mastertenants`.
  Cascade-delete is configured so removing a tenant removes their data.
- API authentication is **per-tenant** via API keys (gRPC metadata
  headers `x-kamsora-tenant`, `x-kamsora-api-key`). Each request resolves
  to a tenant before any storage call.
- The Dashboard.Api enforces tenant scoping in every query; no API path
  is allowed to omit the tenant filter.

A single-organisation self-hosted deployment provisions exactly one
tenant; the multi-tenant machinery imposes negligible overhead in that
mode.

## Consequences

### Positive

- No painful migration ever required to enable SaaS.
- Tenant isolation is a storage-layer guarantee, not an application-layer
  convention — much harder to accidentally leak data across tenants.
- ClickHouse partition pruning by `tenant_id` improves query performance
  even for single-tenant deployments because date+tenant partitions are
  smaller and more selective than date-only.

### Negative / trade-offs

- Every storage migration must include `tenant_id`. We accept this as a
  fixed cost.
- Dashboard.Api code is slightly noisier — every repository method takes a
  `tenantId` argument. We document this convention in
  [CONTRIBUTING.md](../../CONTRIBUTING.md).
- The login flow for the self-hosted case has to provision a default
  tenant out of the box. We will ship a `kamsora-apm seed` CLI in M4.

### Neutral

- Per-tenant rate limits, retention overrides, and quotas all sit on
  `mastertenants` columns — easy to extend without schema gymnastics.

## Alternatives considered

- **Single-tenant forever** — rejected; would foreclose a major use case
  and require a fork to ever support SaaS.
- **Defer multi-tenancy** — rejected; retrofitting partition keys is
  prohibitively expensive once production data exists.

## References

- ClickHouse MergeTree partition / ORDER BY constraints:
  <https://clickhouse.com/docs/en/engines/table-engines/mergetree-family/mergetree>
