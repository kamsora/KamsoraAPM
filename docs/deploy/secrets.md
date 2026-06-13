# Production secrets

KamsoraAPM ships with **development-only secrets** in `appsettings.json` so the
local quickstart works out of the box. In the `Production` environment both the
Collector and the Dashboard API **refuse to start** while any of these are
still present:

| Setting | Dev default | Replace with |
|---|---|---|
| `KamsoraApm:Postgres:ConnectionString` | password `kamsora_dev_only_change_me` | real credentials |
| `KamsoraApm:ClickHouse:ConnectionString` | password `kamsora_dev_only_change_me` | real credentials |
| `KamsoraApm:Auth:JwtSigningKey` (Dashboard API) | well-known placeholder | random 48+ byte secret |
| `KamsoraApm:Auth:SeedTenant:AdminPassword` (Dashboard API) | `ChangeMe!2026` | strong password (or remove the seed block entirely) |

## Supplying real secrets

Use environment variables — they override `appsettings.json` with `__`
(double underscore) as the section separator:

```bash
export KamsoraApm__Postgres__ConnectionString="Host=db;...;Password=<real>"
export KamsoraApm__ClickHouse__ConnectionString="Host=ch;...;Password=<real>"
export KamsoraApm__Auth__JwtSigningKey="$(openssl rand -base64 48)"
```

Docker Compose:

```yaml
services:
  dashboard-api:
    environment:
      KamsoraApm__Auth__JwtSigningKey: ${KAMSORA_JWT_KEY:?set in .env}
```

Kubernetes: mount from a `Secret` via `envFrom` / `secretKeyRef`.

## Why startup fails instead of warning

A warning in the logs is read by nobody until the incident review. A node
that comes up with the repo's JWT signing key accepts **forged admin tokens
from anyone who has read the source**. Failing fast is the only enforcement
that works at fleet scale.
