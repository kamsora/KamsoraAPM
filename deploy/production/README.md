# KamsoraAPM - production deployment

This folder contains everything needed to stand up a production-grade
KamsoraAPM server-side stack on a **single Linux VM** with automatic HTTPS
via Let's Encrypt.

```
deploy/production/
├── docker-compose.production.yml    # The full stack definition
├── Caddyfile                        # TLS termination + reverse proxy config
├── .env.production.example          # Template - copy to .env.production
├── bootstrap.sh                     # One-shot secret generator + preflight
├── backup/
│   ├── Dockerfile                   # Postgres + ClickHouse backup sidecar
│   ├── entrypoint.sh
│   └── backup.sh
└── README.md                        # This file
```

Service Dockerfiles live in [`deploy/docker/`](../docker/) (shared between
dev and prod compose paths):

```
deploy/docker/
├── Dockerfile.collector             # gRPC ingest container
├── Dockerfile.dashboard-api         # REST/JWT backend container
├── Dockerfile.dashboard             # SPA + nginx container
└── dashboard-nginx.conf
```

---

## 1. Prerequisites

| Requirement | Notes |
|---|---|
| **Linux VM** | Ubuntu 22.04 LTS / Debian 12 / RHEL 9 / Amazon Linux 2023. Minimum **4 vCPU, 8 GB RAM, 50 GB disk**. Scales to ~50 hosts + a few hundred apps. |
| **Public IP** | Reachable from the internet on **ports 80, 443, 5080**. |
| **Domain name** | A real DNS hostname (e.g. `apm.example.com`) with an **A-record pointing at the server's public IP**. Wildcard certs are not required. |
| **Docker Engine 24+** | Plus the `docker compose` plugin (built into Docker Desktop and Docker CE 24+). |
| **80 / 443 / 5080 open in firewall** | Caddy needs 80+443 for ACME + dashboard, 5080 for gRPC ingest. |

### Sanity checks before deploying

```bash
# Docker present?
docker --version && docker compose version

# DNS resolves to this server?
dig +short apm.example.com    # → should print this server's IP

# Ports reachable from outside? (run from another machine)
nc -zv apm.example.com 80
nc -zv apm.example.com 443
nc -zv apm.example.com 5080
```

If `dig` returns the wrong IP, **fix DNS first** - Let's Encrypt cannot issue
a cert otherwise and Caddy will retry forever.

---

## 2. Deploy

### 2.1. Clone the repo on the deploy server

```bash
git clone https://github.com/kamsora/KamsoraAPM.git
cd KamsoraAPM/deploy/production
```

### 2.2. Run bootstrap

```bash
chmod +x bootstrap.sh backup/*.sh
./bootstrap.sh
```

The first run:
- Creates `.env.production` from the template.
- Fills `POSTGRES_PASSWORD`, `CLICKHOUSE_PASSWORD`, `JWT_SIGNING_KEY`, and
  `SEED_ADMIN_PASSWORD` with strong random values.
- Tells you what you still need to set by hand (`KAMSORA_DOMAIN`,
  `KAMSORA_ACME_EMAIL`, `SEED_ADMIN_EMAIL`).

### 2.3. Fill the three remaining fields

```bash
nano .env.production
```

```dotenv
KAMSORA_DOMAIN=apm.example.com
KAMSORA_ACME_EMAIL=ops@example.com
SEED_ADMIN_EMAIL=admin@example.com
```

Re-run bootstrap to confirm everything is set:

```bash
./bootstrap.sh
```

It will print the **admin password** for first login. **Copy it now** -
this is the only place it's shown in plaintext.

### 2.4. Bring the stack up

```bash
docker compose --env-file .env.production \
    -f docker-compose.production.yml up -d --build
```

First start takes ~3 minutes:
1. Building the four images (Collector, Dashboard.Api, Dashboard SPA, Backup).
2. Initialising Postgres (applies all SQL migrations from `deploy/sql/postgres/`).
3. Initialising ClickHouse (applies all SQL migrations from `deploy/sql/clickhouse/`).
4. Caddy requesting and installing a Let's Encrypt cert for `KAMSORA_DOMAIN`.
5. Dashboard.Api seeding the first tenant.

Watch progress:

```bash
docker compose --env-file .env.production -f docker-compose.production.yml logs -f
```

When you see `Hosting environment: Production` in the dashboard-api logs and
Caddy's log shows `certificate obtained successfully`, you're live.

### 2.5. Log in

Open `https://<KAMSORA_DOMAIN>/login`. Sign in with the seeded admin email
+ the password from `bootstrap.sh`. **Change the password immediately** via
the **Platform** page (M4.2 ships a "change password" UI; for now use the
SQL update in §7 below).

---

## 3. Configure agents to send to your new server

In the dashboard, go to **API Keys → + Mint new key**. Click the
**HostMonitor** or **Agent** tab in the resulting modal - the install
snippets are now pre-filled with:

- `CollectorEndpoint`: `https://<KAMSORA_DOMAIN>:5080`
- `TenantId`: the auto-seeded tenant's UUID
- `ApiKey`: the freshly minted key

Copy those into your HostMonitor `appsettings.json` on each server,
or into your `.NET` app's `Program.cs` configuration. Within ~60 s of
restart the host/app appears in the dashboard.

---

## 4. Upgrade

```bash
cd KamsoraAPM
git pull

cd deploy/production
docker compose --env-file .env.production \
    -f docker-compose.production.yml build --pull

docker compose --env-file .env.production \
    -f docker-compose.production.yml up -d
```

Containers are recreated only if their image hashes changed. Postgres and
ClickHouse volumes persist across upgrades. **Schema migrations require a
manual step** until M5 - apply any new `deploy/sql/postgres/*.sql` or
`deploy/sql/clickhouse/*.sql` against the running containers:

```bash
# Postgres:
docker exec -i kamsora-apm-postgres-prod \
    psql -U kamsora -d kamsora_apm < ../sql/postgres/0NN_new_migration.sql

# ClickHouse:
docker cp ../sql/clickhouse/0NN_new_migration.sql kamsora-apm-clickhouse-prod:/tmp/
docker exec kamsora-apm-clickhouse-prod clickhouse-client \
    --user kamsora --password "${CLICKHOUSE_PASSWORD}" \
    --multiquery --queries-file /tmp/0NN_new_migration.sql
```

(Use the `CLICKHOUSE_PASSWORD` value from `.env.production`.)

---

## 5. Backups

The `backup` sidecar runs **daily at 03:00 UTC** (configurable via
`BACKUP_CRON_SCHEDULE`). Each run writes to the `kamsora-apm-backups`
docker volume:

```
/backups/postgres/2026-06-03/kamsora_apm.sql.gz
/backups/clickhouse/2026-06-03/kamsora_apm-spans.tsv.gz
/backups/clickhouse/2026-06-03/kamsora_apm-host_cpu_memory.tsv.gz
...
```

Retention is `BACKUP_RETENTION_DAYS` days (default 7). Older folders are pruned.

### Mirror backups off-box

The backups volume is just a folder; mount it from the host or copy nightly:

```bash
# Find the path of the docker volume:
docker volume inspect kamsora-apm-backups --format '{{ .Mountpoint }}'

# rsync nightly to another machine / S3 / etc.
rsync -a --delete \
    "$(docker volume inspect kamsora-apm-backups --format '{{ .Mountpoint }}')/" \
    backup-server:/srv/kamsora-apm-backups/
```

### Restore

```bash
# Postgres:
gunzip < kamsora_apm.sql.gz | \
    docker exec -i kamsora-apm-postgres-prod \
    psql -U kamsora -d kamsora_apm

# ClickHouse (per table - the schema must exist first, init scripts handle this):
gunzip < kamsora_apm-spans.tsv.gz | \
    docker exec -i kamsora-apm-clickhouse-prod \
    clickhouse-client --user kamsora --password "${CLICKHOUSE_PASSWORD}" \
        --query "INSERT INTO kamsora_apm.spans FORMAT TabSeparatedWithNames"
```

---

## 6. Operational notes

### Logs
```bash
docker compose -f docker-compose.production.yml logs -f caddy            # TLS / routing
docker compose -f docker-compose.production.yml logs -f collector        # ingest
docker compose -f docker-compose.production.yml logs -f dashboard-api    # auth / queries
docker compose -f docker-compose.production.yml logs -f dashboard        # nginx access log
docker compose -f docker-compose.production.yml logs -f backup           # nightly backup
```

### Restart a single service
```bash
docker compose --env-file .env.production -f docker-compose.production.yml \
    restart dashboard-api
```

### Stop everything
```bash
docker compose -f docker-compose.production.yml down
```
(Data volumes persist. To wipe everything including the database, add `-v`.)

### Resource limits
Each service has `deploy.resources.limits` configured (see the compose file).
Adjust upward if you see container OOM kills in `docker stats`. Defaults
target a 4 vCPU / 8 GB RAM host.

### TLS cert renewal
Caddy renews automatically ~30 days before expiry. No action needed unless
DNS, ports 80/443, or the ACME email changes - in which case Caddy logs
will show the failure within hours.

---

## 7. First-run cheat sheet - change the seeded admin password

Until the "Change password" UI ships in M4.2, change the seeded password
directly in Postgres:

```bash
# 1. Hash a new password using a small dotnet one-liner from the repo:
docker run --rm -it --network kamsora-apm-internal \
    mcr.microsoft.com/dotnet/sdk:8.0 dotnet fsi --use:- <<'EOF'
open System.Security.Cryptography
let pw = "your-new-password-here"
let salt = RandomNumberGenerator.GetBytes(16)
let h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100000, HashAlgorithmName.SHA256, 32)
printfn "$pbkdf2$100000$%s$%s" (System.Convert.ToBase64String salt) (System.Convert.ToBase64String h)
EOF

# 2. Paste the printed hash into:
docker exec -it kamsora-apm-postgres-prod \
    psql -U kamsora -d kamsora_apm -c \
    "UPDATE masterusers SET password_hash = '<paste-hash-here>' WHERE email = 'admin@example.com';"
```

---

## 8. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Browser shows cert warning | DNS not pointing at this server, or ports 80/443 blocked | `dig +short <domain>` → must match server IP. Open firewall ports. |
| `caddy` logs: `failed to obtain certificate` | Same as above, or ACME email invalid | Fix DNS / firewall / email. Caddy auto-retries. |
| HostMonitor logs: `Status(StatusCode="Unavailable")` | Port 5080 blocked or wrong domain | Confirm `nc -zv <domain> 5080` works from the host. |
| HostMonitor logs: `Unauthenticated` | API key revoked or wrong tenant | Mint a fresh key from the dashboard's **API Keys** page. |
| Dashboard 500 on every page | DB connection failed | `docker compose logs postgres clickhouse` - check passwords match `.env.production`. |
| Disk filling up fast | ClickHouse retention too long, or backups not pruning | Lower `data_retention_days` per tenant; check `BACKUP_RETENTION_DAYS`. |
| Caddy logs are empty | Volume mount permissions | `chmod 755 .` in the production folder. |

---

## 9. What this stack does NOT include (yet)

- **High availability** - single VM only. Multi-node (Postgres replica,
  ClickHouse cluster, multiple Collector replicas behind a real LB) lands in
  the **M5** roadmap milestone.
- **Off-box log shipping** - container logs go to the local Docker
  log driver. If you want them in Loki / CloudWatch / Datadog, attach a
  log-driver to docker.
- **WAF / DDoS protection** - Caddy can do rate limiting and other
  guard-rails but they aren't pre-configured. Front this with Cloudflare
  if exposed to the open internet.
- **Off-box secret storage** - `.env.production` lives on disk. Production
  hardening would pull secrets from Vault / AWS SSM / etc. via a sidecar.

These are all reasonable next steps; ask if you want a milestone for any
of them.
