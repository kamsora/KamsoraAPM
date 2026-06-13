# KamsoraAPM â€” Production Deployment Runbook

A focused, step-by-step guide to deploy the KamsoraAPM server-side stack
on a **single Linux VM** using your **own wildcard TLS cert** and a
**single subdomain** like `apm.example.com`.

This document is a runbook, not a tutorial. Run each numbered step in
order. After every step there's a **verify** check â€” if it passes, move
on; if it fails, jump to Â§11 Troubleshooting.

For background on architecture see [README.md](README.md). For the
Let's Encrypt variant see README Â§2.

---

## Table of contents

0.  [What you're deploying](#0-what-youre-deploying)
1.  [Prerequisites](#1-prerequisites)
2.  [Server preparation](#2-server-preparation)
3.  [Convert your `.pfx` to PEM](#3-convert-your-pfx-to-pem)
4.  [Clone the repo on the server](#4-clone-the-repo-on-the-server)
5.  [Upload the certs to the server](#5-upload-the-certs-to-the-server)
6.  [Configure secrets and domain](#6-configure-secrets-and-domain)
7.  [Deploy the stack](#7-deploy-the-stack)
8.  [Verify the deploy](#8-verify-the-deploy)
9.  [First login and mint API keys](#9-first-login-and-mint-api-keys)
10. [Install your first agent](#10-install-your-first-agent)
11. [Troubleshooting (errors and fixes)](#11-troubleshooting-errors-and-fixes)
12. [Day-2 operations](#12-day-2-operations)
13. [What this deploy does NOT include](#13-what-this-deploy-does-not-include)

---

## 0. What you're deploying

A single host runs seven containers fronted by Caddy with your wildcard cert:

```
Internet
   â”‚
   â-¼
â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
â”‚  Server VM (Ubuntu 22.04 / Debian 12)                            â”‚
â”‚                                                                  â”‚
â”‚   :443 â”€â”€ Caddy â”€â”€ /          â”€â”€â-º dashboard  (nginx + SPA)       â”‚
â”‚            â”‚       /api/*     â”€â”€â-º dashboard-api (.NET REST)      â”‚
â”‚            â”‚                                                     â”‚
â”‚   :5080 â”€â”€ Caddy â”€â”€â-º collector (gRPC ingest, .NET)               â”‚
â”‚                                                                  â”‚
â”‚            â-¼                                                     â”‚
â”‚         postgres â-„â”€â”€ dashboard-api & collector (auth + audit)    â”‚
â”‚         clickhouse â-„ dashboard-api & collector (time-series)     â”‚
â”‚                                                                  â”‚
â”‚         backup (sidecar) â”€â”€ daily pg_dump + ClickHouse dump      â”‚
â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
   â-²
   â”‚
   â”œâ”€â”€ browser users      â†’ https://apm.example.com/
   â”œâ”€â”€ instrumented apps  â†’ https://apm.example.com:5080  (.NET Agent)
   â””â”€â”€ monitored hosts    â†’ https://apm.example.com:5080  (HostMonitor)
```

Everything inside the box is on a private docker network. Only Caddy
listens on host ports. There is no direct path from the internet to
PostgreSQL, ClickHouse, or any of the .NET containers.

---

## 1. Prerequisites

| # | Item | Detail |
|---|---|---|
| 1.1 | **Linux VM** | Ubuntu 22.04 LTS or Debian 12. Minimum **4 vCPU, 8 GB RAM, 50 GB SSD**. Comfortably handles ~50 hosts + a few hundred apps. |
| 1.2 | **Public IPv4** with inbound ports **443, 5080** open in cloud security group + OS firewall. Port 80 is optional (only needed if you keep the HTTPâ†’HTTPS redirect block in the Caddyfile). |
| 1.3 | **Domain** you control: full subdomain like `apm.example.com`. |
| 1.4 | **Wildcard TLS cert** (`.pfx` file) whose subject covers your subdomain. Wildcard like `*.example.com` works; an exact-domain cert for `apm.example.com` also works. |
| 1.5 | **PFX password** (the one your issuer set when generating the file). |
| 1.6 | **DNS A-record** `apm.example.com â†’ <VM public IP>`, already propagated. Verify with `dig +short apm.example.com` â€” must return your VM IP. |
| 1.7 | **SSH access** to the VM with sudo. |
| 1.8 | **Local machine** with `openssl` (for `.pfx` conversion) and `scp` (or any way to upload files). |

If any of 1.1â€“1.7 are not yet true, fix them **before** continuing. The
biggest source of failed deploys is missing DNS or blocked ports.

---

## 2. Server preparation

SSH into the VM as a sudoer.

### 2.1. Install Docker Engine + Compose plugin

```bash
# One-line installer from get.docker.com (idempotent, safe to re-run).
curl -fsSL https://get.docker.com | sudo sh

# Add your user to the docker group so you don't need sudo for every command.
sudo usermod -aG docker $USER

# Re-login or run this so the group takes effect in the current shell.
newgrp docker
```

**Verify:**
```bash
docker --version          # Docker version 25.x or newer
docker compose version    # Docker Compose version v2.x or newer
docker run --rm hello-world   # prints "Hello from Docker!"
```

### 2.2. Open firewall ports

If your VM has `ufw` enabled:

```bash
sudo ufw allow 22/tcp        # don't lock yourself out
sudo ufw allow 443/tcp       # dashboard HTTPS
sudo ufw allow 5080/tcp      # gRPC ingest
# Optional â€” only if you want HTTPâ†’HTTPS redirect on port 80:
sudo ufw allow 80/tcp
sudo ufw enable
sudo ufw status
```

If your VM has `firewalld`:

```bash
sudo firewall-cmd --permanent --add-port=443/tcp
sudo firewall-cmd --permanent --add-port=5080/tcp
sudo firewall-cmd --reload
```

Cloud security group (AWS / DigitalOcean / Hetzner / Azure) â€” open the
same ports there too. The cloud firewall is in addition to the OS one.

**Verify from another machine:**
```bash
nc -zv apm.example.com 443
nc -zv apm.example.com 5080
# Both should print "succeeded".
```

### 2.3. Set the system clock

TLS will fail if the VM clock is more than a few minutes off.

```bash
sudo timedatectl set-ntp true
timedatectl status     # System clock synchronized: yes
```

---

## 3. Convert your `.pfx` to PEM

You can run this on **your local machine** (recommended â€” keeps the `.pfx`
file off the server) or on the VM itself.

### 3.1. On your local machine (Windows with Git Bash, macOS, or Linux)

```bash
# In a temp folder.
mkdir -p ~/kamsora-deploy/certs
cd ~/kamsora-deploy

openssl pkcs12 -in /path/to/your-wildcard.pfx -out certs/cert.pem -clcerts -nokeys
openssl pkcs12 -in /path/to/your-wildcard.pfx -out certs/key.pem  -nocerts -nodes

chmod 600 certs/key.pem
chmod 644 certs/cert.pem
```

You'll be prompted for the PFX password twice (once per `openssl` call).

### 3.2. Verify the cert

```bash
openssl x509 -in certs/cert.pem -noout -subject -issuer -dates
```

Expected output (issuer + dates will differ):

```
subject= CN=*.example.com
issuer=  CN=DigiCert TLS RSA SHA256 2020 CA1
notBefore=Jan  1 00:00:00 2026 GMT
notAfter=Jan  1 23:59:59 2027 GMT
```

**Check three things:**

- `subject` covers your target domain. `*.example.com` covers `apm.example.com`. An exact `CN=apm.example.com` is also fine.
- `notAfter` is in the future (not expired).
- Verify cert + key match:
  ```bash
  openssl x509 -in certs/cert.pem -noout -modulus | openssl md5
  openssl rsa  -in certs/key.pem  -noout -modulus | openssl md5
  # Both md5 hashes must be identical.
  ```

If any of these fail, fix before continuing (re-export from issuer / use
the correct `.pfx`).

---

## 4. Clone the repo on the server

SSH into the VM.

```bash
cd ~
git clone https://github.com/kamsora/KamsoraAPM.git
cd KamsoraAPM/deploy/production
```

**Verify:**
```bash
ls
# Should list: Caddyfile, Caddyfile.byo-cert, README.md, DEPLOY-GUIDE.md,
# docker-compose.production.yml, docker-compose.byo-cert.yml,
# bootstrap.sh, convert-pfx.sh, backup/, .env.production.example
```

---

## 5. Upload the certs to the server

From your **local machine** (where the converted PEM files are):

```bash
# Replace ubuntu@<vm-ip> with your VM's SSH details.
scp -r ~/kamsora-deploy/certs ubuntu@apm.example.com:KamsoraAPM/deploy/production/
```

Back on the **VM**:

```bash
cd ~/KamsoraAPM/deploy/production

# Tighten permissions â€” key should be unreadable to non-owners.
chmod 600 certs/key.pem
chmod 644 certs/cert.pem
ls -la certs/
```

**Verify:**

```bash
# Re-run the cert verification from Â§3.2 to make sure the upload didn't corrupt anything.
openssl x509 -in certs/cert.pem -noout -subject -issuer -dates
```

---

## 6. Configure secrets and domain

```bash
cd ~/KamsoraAPM/deploy/production
chmod +x bootstrap.sh convert-pfx.sh backup/*.sh
./bootstrap.sh
```

First run will:

- Create `.env.production` from the example template
- Generate strong random values for `POSTGRES_PASSWORD`, `CLICKHOUSE_PASSWORD`, `JWT_SIGNING_KEY`, `SEED_ADMIN_PASSWORD`
- Print which fields still need a human value (DOMAIN / EMAIL / ADMIN_EMAIL)

Now edit `.env.production`:

```bash
nano .env.production
```

Set these three values:

```dotenv
KAMSORA_DOMAIN=apm.example.com           # your subdomain
KAMSORA_ACME_EMAIL=                       # leave EMPTY â€” not used in BYO mode
SEED_ADMIN_EMAIL=admin@example.com        # your first dashboard login email
```

Save and re-run bootstrap:

```bash
./bootstrap.sh
```

Output will end with a banner like:

```
========================================================================
 KamsoraAPM bootstrap complete.

   Dashboard URL  : https://apm.example.com/
   Admin email    : admin@example.com
   Admin password : kjsdhf83hf283hf
   â¬† COPY THIS NOW â€” it will NOT be shown again.
========================================================================
```

**Save the admin password in your password manager immediately**. The
seeded password lives only in `.env.production` (chmod 600) until first
login. Change it from the dashboard after first login.

---

## 7. Deploy the stack

This is the actual "go" moment.

```bash
docker compose --env-file .env.production \
    -f docker-compose.production.yml \
    -f docker-compose.byo-cert.yml \
    up -d --build
```

What happens:

| Phase | ~Duration | What's happening |
|---|---|---|
| Image builds | 2â€“4 min | Multi-stage builds of Collector, Dashboard.Api, Dashboard SPA, Backup sidecar |
| Pull base images | 30â€“60 s | postgres:16-alpine, clickhouse-server:24.10, caddy:2.8, nginx |
| Postgres init | 10â€“20 s | First-boot runs all `deploy/sql/postgres/*.sql` migrations |
| ClickHouse init | 10â€“20 s | First-boot runs all `deploy/sql/clickhouse/*.sql` migrations |
| Service start | 5â€“10 s | Collector, Dashboard.Api, Caddy, Dashboard, Backup |
| Caddy cert load | < 1 s | Reads `certs/cert.pem` + `certs/key.pem` |
| Tenant seeding | 1â€“2 s | Dashboard.Api creates first tenant + admin user + initial API key |

**Total ~3â€“4 minutes** for the very first deploy. Subsequent restarts are
under 30 seconds because images are cached.

Watch progress:

```bash
docker compose --env-file .env.production \
    -f docker-compose.production.yml \
    -f docker-compose.byo-cert.yml \
    logs -f
```

You're "done" when you see, in any order:

- `postgres`: `database system is ready to accept connections`
- `clickhouse`: `Application: Ready for connections`
- `dashboard-api`: `Now listening on: http://[::]:5090` + `Hosting environment: Production`
- `dashboard-api`: a log line containing **`api_key : kapm_...`** (your seeded ingest key â€” **save this**)
- `collector`: `Now listening on: http://[::]:5080`
- `caddy`: `server running` (no certificate-loading errors)
- `backup`: `Schedule installed: 0 3 * * *`

Press `Ctrl+C` to exit the log tail (containers keep running).

---

## 8. Verify the deploy

### 8.1. All containers healthy

```bash
docker compose --env-file .env.production \
    -f docker-compose.production.yml \
    -f docker-compose.byo-cert.yml \
    ps
```

Every row's `STATUS` column should show `Up <time> (healthy)` (or just
`Up` for services without an explicit healthcheck). No `Exited` or
`Restarting` rows.

### 8.2. HTTPS to the dashboard

From the VM:

```bash
curl -fsSv https://apm.example.com/healthz
```

Expected: HTTP 200 with body `{"status":"ok","component":"kamsora-apm-dashboard-api"}`.
The verbose flag also prints the cert chain â€” confirm the issuer matches
your wildcard cert (not Let's Encrypt).

From a browser on your laptop, open `https://apm.example.com/`:

- Green padlock with your real cert
- Login page renders

### 8.3. gRPC ingest port

From any machine outside the VM:

```bash
openssl s_client -connect apm.example.com:5080 -servername apm.example.com 2>&1 | head -25
```

Expected output includes:

```
Server certificate
subject= CN=*.example.com
issuer=  CN=DigiCert TLS RSA SHA256 2020 CA1
...
SSL handshake has read ... bytes and written ... bytes
Verification: OK
```

That confirms port 5080 is reachable, presenting your wildcard cert, and
the TLS handshake completes successfully.

If Â§8.1, 8.2, and 8.3 all pass â€” your stack is live.

---

## 9. First login and mint API keys

### 9.1. Log in

1. Open `https://apm.example.com/login` in a browser.
2. Email = your `SEED_ADMIN_EMAIL` (e.g. `admin@example.com`).
3. Password = the one printed by `bootstrap.sh` in Â§6.

You land on the **Overview** dashboard (empty â€” no telemetry yet).

### 9.2. Change the admin password (highly recommended)

Until the in-app "change password" UI ships in M4.2, change it via
Postgres. From the VM:

```bash
# Hash a new password using openssl + a tiny shell script
NEW_PW='your-strong-new-password-here'

# Generate the PBKDF2 hash the same way the auth code does:
docker run --rm -i mcr.microsoft.com/dotnet/sdk:8.0 dotnet fsi --use:- <<EOF
open System.Security.Cryptography
let pw = "$NEW_PW"
let salt = RandomNumberGenerator.GetBytes(16)
let h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100000, HashAlgorithmName.SHA256, 32)
printfn "\$pbkdf2\$100000\$%s\$%s" (System.Convert.ToBase64String salt) (System.Convert.ToBase64String h)
EOF
```

Copy the printed hash (the line starting with `$pbkdf2$100000$...`) and:

```bash
docker exec -it kamsora-apm-postgres-prod \
    psql -U kamsora -d kamsora_apm -c \
    "UPDATE masterusers SET password_hash = '<PASTE_HASH_HERE>' WHERE email = 'admin@example.com';"
```

Log out and back in with the new password to confirm.

### 9.3. Mint your first ingest API key

In the dashboard sidebar click **API Keys â†’ + Mint new key**.

Fill in:
- Name: `prod-default` (or whatever label you want)
- Scopes: `ingest` (default)

Click **Mint key**. A modal pops with the cleartext key â€” **copy it now**,
it won't be shown again. The modal also has an **Install snippets** tab
strip with HostMonitor and Agent install code pre-filled with:

- `CollectorEndpoint`: `https://apm.example.com:5080`
- `TenantId`: your auto-seeded tenant UUID
- `ApiKey`: the cleartext you just minted

Copy those snippets â€” they go into your agents' config.

---

## 10. Install your first agent

You have v0.1 binaries already built at
`releases/v0.1/hostmonitor/{win-x64,linux-x64}/` plus install scripts in
`releases/v0.1/scripts/`.

### 10.1. Install HostMonitor on a Windows server

On the target Windows machine, as **Administrator**:

```powershell
# Assumes you copied releases/v0.1/ to C:\Temp\kamsora-v0.1\
cd C:\Temp\kamsora-v0.1\scripts

.\install-windows.ps1 `
    -CollectorEndpoint "https://apm.example.com:5080" `
    -TenantId          "<paste-tenant-uuid>" `
    -ApiKey            "<paste-kapm_-key>"
```

The script creates `C:\Program Files\Kamsora\HostMonitor\`, writes
appsettings, registers the Windows Service, and starts it. Within
~60 seconds the host appears on the dashboard's **Hosts** page.

### 10.2. Install HostMonitor on a Linux server

```bash
# Assumes you copied releases/v0.1/ to /opt/kamsora-v0.1/
cd /opt/kamsora-v0.1/scripts

sudo ./install-linux.sh \
    --collector https://apm.example.com:5080 \
    --tenant    <paste-tenant-uuid> \
    --apikey    <paste-kapm_-key>
```

The script creates the `kamsora` system user, installs to
`/opt/kamsora-host-monitor/`, writes the systemd unit, and starts it.
Within ~60 seconds the host appears on the dashboard.

### 10.3. Install the .NET Agent in your app

Once `KamsoraAPM.Agent` is published to nuget.org (see
[releases/v0.1/README.md](../../releases/v0.1/README.md)):

```powershell
dotnet add package KamsoraAPM.Agent
```

In `Program.cs`:

```csharp
builder.Services.AddKamsoraApm(o =>
{
    o.CollectorEndpoint = "https://apm.example.com:5080";
    o.TenantId          = "<paste-tenant-uuid>";
    o.ApiKey            = "<paste-kapm_-key>";
    o.ServiceName       = "my-app-name";
});
```

Rebuild + redeploy your app. Spans appear on the dashboard's **Traces**
page within seconds.

---

## 11. Troubleshooting (errors and fixes)

### Deploy-time errors

| Error / Symptom | Likely cause | Fix |
|---|---|---|
| `required variable POSTGRES_PASSWORD is missing a value` | Ran compose without `--env-file .env.production`, or you used the `.env.production.example` template (which has empty values by design) | Use `--env-file .env.production` (the real file produced by `bootstrap.sh`). |
| `Bind for 0.0.0.0:443 failed: port is already allocated` | Another service (Apache, nginx, system Caddy) on the VM is using port 443 | `sudo systemctl stop nginx apache2 caddy 2>/dev/null; sudo lsof -iTCP:443 -sTCP:LISTEN` to find the process, then stop/disable it. |
| `Cannot connect to the Docker daemon at unix:///var/run/docker.sock` | Docker not running, or you forgot `newgrp docker` after `usermod` | `sudo systemctl start docker` + re-login. |
| `permission denied while trying to connect to the Docker daemon socket` | Your user not in `docker` group | `sudo usermod -aG docker $USER` then log out + log in. |
| `pull access denied for kamsora-apm-collector` | Compose tried to pull a locally-built tag from a registry | You're missing `--build`. Re-run with `up -d --build`. |
| Build fails with `Could not load file or assembly` | Stale Docker build cache | `docker compose -f ... build --no-cache` |

### Caddy / TLS errors

| Symptom | Fix |
|---|---|
| Caddy logs: `loading certificate: open /certs/cert.pem: no such file or directory` | The `certs/` folder is missing or in the wrong place. Must be at `deploy/production/certs/` relative to the compose file. |
| Caddy logs: `tls: private key does not match public key` | `cert.pem` and `key.pem` came from different `.pfx` files. Re-run `convert-pfx.sh` and replace both. |
| Browser: `NET::ERR_CERT_AUTHORITY_INVALID` | Cert chain is missing intermediates. Re-run the `.pfx` â†’ PEM step but include the chain: `openssl pkcs12 -in your.pfx -out cert.pem -nokeys` (without `-clcerts` â€” exports the full chain). |
| Browser: `NET::ERR_CERT_COMMON_NAME_INVALID` | Cert subject doesn't match `KAMSORA_DOMAIN`. Check Â§3.2 â€” `subject=` must be `*.example.com` (or matching). Either rename DNS to match the cert or get a new cert. |
| Browser: `NET::ERR_CERT_DATE_INVALID` | Cert expired or system clock wrong. Check `openssl x509 -in certs/cert.pem -noout -dates` and `date -u`. |
| Caddy fails to start, complains about port 80 | You left the `:80` redirect block in `Caddyfile.byo-cert` but port 80 is blocked. Either open port 80 in the firewall or comment out the `:80 { redir ... }` block in the Caddyfile. |

### Database / migration errors

| Symptom | Fix |
|---|---|
| `dashboard-api` keeps restarting, logs say `relation "mastertenants" does not exist` | Postgres init scripts didn't run (volume was already populated from a previous attempt). Fix: `docker compose down -v` (wipes data) then redeploy. Use only on a fresh attempt â€” wipes everything. |
| `Code: 60. DB::Exception: Table kamsora_apm.spans doesn't exist` | ClickHouse init didn't run. Same fix as above â€” `docker compose down -v` then redeploy. |
| `dashboard-api`: `password authentication failed for user "kamsora"` | `POSTGRES_PASSWORD` in `.env.production` doesn't match what Postgres was initialised with (init only runs once). Either change `.env.production` back to the original generated value, or wipe + redeploy. |
| Caddy 502 on `/api/*` | `dashboard-api` container down. `docker compose logs dashboard-api` â€” most common cause is the DB password mismatch above. |

### Runtime / agent errors

| Symptom | Fix |
|---|---|
| Agent logs: `Status(StatusCode="Unavailable", Detail="connection refused")` | Port 5080 not reachable. From the agent host run `nc -zv apm.example.com 5080`. Fix the firewall hop that's blocking. |
| Agent logs: `Status(StatusCode="Unauthenticated", Detail="Invalid tenant or API key")` | Key revoked, tenant wrong, or you're using a key from a different deploy. Re-mint from the dashboard. |
| Agent logs: `tls: failed to verify certificate: x509: certificate signed by unknown authority` | Your cert's CA isn't in the agent machine's trust store. For corporate-CA certs, install the CA bundle on the agent machine. For public CAs (DigiCert / GoDaddy) this should never happen â€” likely the chain is missing (see Caddy section above). |
| Dashboard "No hosts in this time range" but you installed HostMonitor minutes ago | First batch takes 60 s (6 snapshots Ã- 10 s). On the host: `systemctl status kamsora-host-monitor` / Event Viewer for KamsoraAPM.HostMonitor. Look for `gRPC` errors in those logs. |
| 500 errors on dashboard `/api/v1/hosts/.../disks` etc. | Ran an older Dashboard.Api version. `docker compose -f ... build --pull && docker compose -f ... up -d` to refresh. |

### Disk filling fast

| Cause | Fix |
|---|---|
| Backups not pruning | Check `BACKUP_RETENTION_DAYS` in `.env.production` (default 7). Manually clean older folders in the `kamsora-apm-backups` volume. |
| ClickHouse spans table growing unbounded | Per-tenant `data_retention_days` defaults to 14. Check + lower if needed: `docker exec -it kamsora-apm-postgres-prod psql -U kamsora -d kamsora_apm -c "SELECT tenant_slug, data_retention_days FROM mastertenants;"` Reduce via `UPDATE mastertenants SET data_retention_days = 7 WHERE ...` and the ClickHouse TTL eventually catches up. |
| `host_processes` table growing fast (top 50 procs Ã- 10 s) | Per design â€” `host_processes` already has a shorter `TTL toDateTime(timestamp) + INTERVAL 7 DAY` set in the schema. If it's still too much, drop `TopProcesses` in HostMonitor's `appsettings.json` from 50 â†’ 20. |

### "I just want to start over"

Wipes ALL data (Postgres, ClickHouse, certs, backups). Use only on a
non-production stack:

```bash
cd ~/KamsoraAPM/deploy/production
docker compose --env-file .env.production \
    -f docker-compose.production.yml \
    -f docker-compose.byo-cert.yml \
    down -v
rm -rf certs   # if you want to re-upload too
```

Then start again from Â§3.

---

## 12. Day-2 operations

### 12.1. View logs for one service

```bash
docker compose --env-file .env.production \
    -f docker-compose.production.yml \
    -f docker-compose.byo-cert.yml \
    logs -f dashboard-api    # or caddy, collector, postgres, clickhouse, backup
```

### 12.2. Restart a single service

```bash
docker compose --env-file .env.production \
    -f docker-compose.production.yml \
    -f docker-compose.byo-cert.yml \
    restart caddy
```

### 12.3. Upgrade after `git pull`

```bash
cd ~/KamsoraAPM
git pull

cd deploy/production
docker compose --env-file .env.production \
    -f docker-compose.production.yml \
    -f docker-compose.byo-cert.yml \
    build --pull

docker compose --env-file .env.production \
    -f docker-compose.production.yml \
    -f docker-compose.byo-cert.yml \
    up -d
```

Containers whose image hash hasn't changed don't restart. Postgres /
ClickHouse data volumes persist. **If a new release added SQL
migrations**, apply them manually:

```bash
# Postgres:
docker exec -i kamsora-apm-postgres-prod \
    psql -U kamsora -d kamsora_apm < ../sql/postgres/0NN_new_migration.sql

# ClickHouse:
docker cp ../sql/clickhouse/0NN_new_migration.sql kamsora-apm-clickhouse-prod:/tmp/
docker exec kamsora-apm-clickhouse-prod clickhouse-client \
    --user kamsora \
    --password "$(grep CLICKHOUSE_PASSWORD .env.production | cut -d= -f2)" \
    --multiquery --queries-file /tmp/0NN_new_migration.sql
```

(Automated migration runner ships in M5.)

### 12.4. Rotate the TLS cert (annual)

When your issuer sends a new `.pfx`:

```bash
# On local machine, convert the new pfx
./convert-pfx.sh /path/to/new-wildcard.pfx --password "..."

# scp the new certs to the server, overwriting:
scp -r certs/* ubuntu@apm.example.com:KamsoraAPM/deploy/production/certs/

# On the server, hot-reload Caddy (zero downtime, no restart):
cd ~/KamsoraAPM/deploy/production
docker compose --env-file .env.production \
    -f docker-compose.production.yml \
    -f docker-compose.byo-cert.yml \
    kill -s SIGUSR1 caddy
```

Browsers and agents immediately see the new cert on next connection.

### 12.5. Backups

Daily at 03:00 UTC the backup sidecar writes to the `kamsora-apm-backups`
docker volume. To mirror off-box nightly:

```bash
# Find the volume path on the host:
BACKUP_PATH=$(docker volume inspect kamsora-apm-backups --format '{{.Mountpoint}}')

# Add to root's crontab â€” rsync to another machine each night at 04:00:
sudo crontab -e
# Append:
0 4 * * * rsync -a --delete $BACKUP_PATH/ backup-server:/srv/kamsora-apm-backups/
```

Or mirror to S3 with `aws s3 sync`, etc.

### 12.6. Restore

See README Â§5 (Restore section). Short version:

```bash
# Postgres:
gunzip < kamsora_apm.sql.gz | docker exec -i kamsora-apm-postgres-prod psql -U kamsora -d kamsora_apm

# ClickHouse, per table:
gunzip < kamsora_apm-spans.tsv.gz | \
    docker exec -i kamsora-apm-clickhouse-prod clickhouse-client \
        --user kamsora --password "$(grep CLICKHOUSE_PASSWORD .env.production | cut -d= -f2)" \
        --query "INSERT INTO kamsora_apm.spans FORMAT TabSeparatedWithNames"
```

---

## 13. What this deploy does NOT include

Honest scope statement so you know where the limits are:

| Capability | Status | Planned in |
|---|---|---|
| Single-host docker-compose | âœ… shipped | this guide |
| BYO wildcard cert | âœ… shipped | this guide |
| Daily backups + retention | âœ… shipped | this guide |
| Tenant onboarding UI + API keys UI | âœ… shipped (M4.1) | dashboard |
| Multi-user invite + non-owner roles | âŒ | M4.2 |
| Audit-log viewer UI | âŒ (audit data is recorded, viewer is SQL-only) | M4.2 |
| Tenant suspend / soft-delete UI | âŒ | M4.2 |
| Automated SQL migration runner | âŒ (currently manual) | M5 |
| Auth-token cache for ingest scale | âŒ (every gRPC call hits PG) | M5 |
| Horizontal Collector scaling + LB | âŒ (single Collector container) | M5 |
| ClickHouse cluster (3-node + Distributed) | âŒ (single node) | M5 |
| Per-tenant rate limits | âŒ (no enforcement; limits are advisory in `mastertenants`) | M5 |
| Per-tenant retention UI | âŒ (set via SQL UPDATE on `mastertenants.data_retention_days`) | M6 |
| Off-box backup target (S3 / GCS) | âŒ (local volume only; manual rsync needed) | M6 |
| WAF / DDoS protection | âŒ (front with Cloudflare if needed) | external |
| HA / multi-VM / k8s Helm chart | âŒ | M7+ |
| Apitally-style per-API-key consumer analytics + monetization | âŒ | M6+ (see references section) |

### Reference platforms for the M5+ roadmap

Useful comparison points when prioritising next features:

- **[Apitally](https://apitally.io/)** â€” minimal-instrument APM
  focused on per-API-key consumer analytics, 4xx breakdowns, and traffic
  reports. Their "consumer tracking" and "validation errors" features
  are good targets for KamsoraAPM's M6 sprint.
- **[OpenTelemetry Collector](https://opentelemetry.io/docs/collector/)** â€”
  reference for ingest scaling patterns (M5).
- **[Sentry self-hosted](https://github.com/getsentry/self-hosted)** â€”
  reference for compose-based deployments at the scale we're targeting.
- **[ClickHouse Cloud architecture](https://clickhouse.com/docs/en/cloud-overview)** â€”
  reference for sharding by `tenant_id` in M5.

---

## Quick reference card

```bash
# Up:
docker compose --env-file .env.production -f docker-compose.production.yml -f docker-compose.byo-cert.yml up -d --build

# Down (keeps data):
docker compose --env-file .env.production -f docker-compose.production.yml -f docker-compose.byo-cert.yml down

# Logs:
docker compose --env-file .env.production -f docker-compose.production.yml -f docker-compose.byo-cert.yml logs -f [service]

# Status:
docker compose --env-file .env.production -f docker-compose.production.yml -f docker-compose.byo-cert.yml ps

# Hot-reload cert (no downtime):
docker compose --env-file .env.production -f docker-compose.production.yml -f docker-compose.byo-cert.yml kill -s SIGUSR1 caddy

# Full reset (DESTROYS DATA):
docker compose --env-file .env.production -f docker-compose.production.yml -f docker-compose.byo-cert.yml down -v
```

**Domain**: `apm.example.com`
**Browser**: `https://apm.example.com/`
**Agent endpoint**: `https://apm.example.com:5080`
**Ports**: 443 (dashboard), 5080 (gRPC ingest), 80 (optional redirect)
