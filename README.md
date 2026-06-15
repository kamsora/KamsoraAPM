<div align="center">

<img src="assets/kamsora-icon.png" alt="KamsoraAPM logo" width="120" height="120" />

# KamsoraAPM

**Open-source, self-hosted Application Performance Monitoring for .NET** - distributed traces, logs, metrics, and host telemetry in one multi-tenant dashboard, speaking the OpenTelemetry (OTLP) wire format.

[![NuGet](https://img.shields.io/nuget/v/KamsoraAPM.Agent?logo=nuget&label=KamsoraAPM.Agent)](https://www.nuget.org/packages/KamsoraAPM.Agent/)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![OTLP](https://img.shields.io/badge/Wire-OTLP_compatible-7C3AED)](https://opentelemetry.io/docs/specs/otlp/)
[![CI](https://github.com/kamsora/KamsoraAPM/actions/workflows/ci.yml/badge.svg)](https://github.com/kamsora/KamsoraAPM/actions/workflows/ci.yml)

</div>

<p align="center">
  <img src="assets/screenshots/overview-01.png" alt="KamsoraAPM dashboard - Overview" width="900" />
</p>

<p align="center"><a href="#quick-start-docker"><b>Quick start</b></a> &middot; <a href="#screenshots">Screenshots</a> &middot; <a href="#architecture">Architecture</a> &middot; <a href="docs/ingest/otlp.md">Send from any language</a> &middot; <a href="docs/adr/">Design decisions</a></p>

KamsoraAPM unifies the signals you actually debug with:

- **Distributed tracing** - every HTTP request, database query
  (SqlClient / Npgsql / MySqlConnector / EF Core), and outbound `HttpClient`
  call as a span tree, with per-route latency percentiles.
- **Logs with trace correlation** - `ILogger` output lands next to its trace;
  open a trace and see exactly the log lines it produced, and vice versa.
- **Metrics** - runtime (GC, ThreadPool, exceptions) plus your custom
  `Meter`s, with per-tenant cardinality protection built in.
- **Host monitoring** - CPU, RAM, disk, and network per machine via a tiny
  daemon (Linux systemd / Windows Service).
- **Service map** - a live dependency graph (services, databases, external
  HTTP hosts) drawn automatically from trace data.
- **Consumer analytics & errors** - see which API consumer (JWT claim,
  header, or client IP) drives which traffic and which failures.
- **Alerting** - threshold rules, webhook/email channels, alert history, and
  in-app notifications.
- **Multi-tenant from day one** - tenants, roles, invites, API keys, audit
  log, and a platform-admin plane, ready for both single-org self-hosting
  and hosted offerings.

**Not just .NET:** the Collector also exposes the standard OTLP/gRPC
services, so any OpenTelemetry SDK - Java, Go, Python, Node, Rust - can send
traces, logs, and metrics to KamsoraAPM. See [docs/ingest/otlp.md](docs/ingest/otlp.md).

---

## Screenshots

<table>
  <tr>
    <td width="50%" valign="top">
      <b>Distributed tracing</b><br/>
      <sub>Every request as a span tree, with a full trace-detail drill-down.</sub><br/><br/>
      <img src="assets/screenshots/traces-02.png" alt="Distributed tracing" width="100%" />
    </td>
    <td width="50%" valign="top">
      <b>Live service map</b><br/>
      <sub>Dependency graph drawn automatically from trace data.</sub><br/><br/>
      <img src="assets/screenshots/service-map.png" alt="Service map" width="100%" />
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <b>Metrics</b><br/>
      <sub>Runtime and custom meters, per-tenant cardinality-safe.</sub><br/><br/>
      <img src="assets/screenshots/metrics.png" alt="Metrics" width="100%" />
    </td>
    <td width="50%" valign="top">
      <b>Host monitoring</b><br/>
      <sub>CPU, memory, disk, network and top processes per machine.</sub><br/><br/>
      <img src="assets/screenshots/host-01.png" alt="Host monitoring" width="100%" />
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <b>Errors</b><br/>
      <sub>Failing routes ranked by volume, with full error drill-down.</sub><br/><br/>
      <img src="assets/screenshots/errors.png" alt="Errors" width="100%" />
    </td>
    <td width="50%" valign="top">
      <b>Consumer analytics</b><br/>
      <sub>See which API consumer drives which traffic and failures.</sub><br/><br/>
      <img src="assets/screenshots/consumers.png" alt="Consumer analytics" width="100%" />
    </td>
  </tr>
</table>

---

## Architecture

Four independently deployable components in one monorepo:

| Component | Purpose | Tech |
| --- | --- | --- |
| `KamsoraAPM.Agent` | NuGet package for your Web API. Auto-instruments HTTP, DB clients, `HttpClient`; ships traces/logs/metrics over gRPC with gzip + optional head sampling. | .NET 8, `ActivityListener`, OpenTelemetry instrumentation |
| `KamsoraAPM.HostMonitor` | OS daemon collecting host + per-process metrics. | .NET 8 Worker Service |
| `KamsoraAPM.Collector` | gRPC ingestion (Kamsora + standard OTLP endpoints). Validates API keys, buffers via channels, bulk-writes to ClickHouse. Runs schema migrations and data-retention sweeps automatically. | ASP.NET Core gRPC, raw ADO.NET |
| `KamsoraAPM.Dashboard` | React SPA (Overview, Traces, Service Map, Logs, Metrics, Hosts, Consumers, Errors, Alerts, Admin) backed by `KamsoraAPM.Dashboard.Api` (REST + JWT). | React 19 + Vite + ECharts |

Storage: **ClickHouse** for telemetry (columnar, partitioned per tenant/day,
pre-aggregated rollups), **PostgreSQL** for metadata (tenants, users, API
keys, alert rules).

> Design rule: the ingestion path uses raw ADO.NET only - no ORM between a
> span and the database.

---

## Quick start (Docker)

```bash
git clone https://github.com/kamsora/KamsoraAPM.git
cd KamsoraAPM
```

**1. Generate secrets.** The stack runs in the Production environment and
refuses to start with the repository's default secrets, so generate real ones
first. This script writes `deploy/docker/.env` with cryptographically random
database passwords + JWT key and prints the first-run admin login:

```bash
# Linux / macOS
./scripts/init-env.sh
```
```powershell
# Windows (PowerShell)
powershell -ExecutionPolicy Bypass -File scripts/init-env.ps1
```

> Prefer to set them by hand? Copy `deploy/docker/.env.example` to
> `deploy/docker/.env` (`copy` on Windows CMD, `cp` on Linux/macOS) and replace
> **every** value - the apps reject any leftover default in Production.

**2. Bring up the stack** - databases, Collector, API, and dashboard:

```bash
docker compose -f deploy/docker/docker-compose.yml --env-file deploy/docker/.env --profile apps up -d --build
```

| URL | What |
| --- | --- |
| http://localhost:3000 | Dashboard - log in with the seed admin from `.env` |
| http://localhost:5080 | Collector gRPC ingest (Agent + OTLP, HTTP/2) |
| http://localhost:5081/healthz | Collector health (`/readyz`, `/stats` too) |

On first start the API seeds your tenant and prints a ready-made ingestion
API key once in the `kamsora-apm-dashboard-api` container logs (you can also
mint keys later under **API Keys** in the dashboard):

```bash
docker logs kamsora-apm-dashboard-api 2>&1 | grep -i "api key"
```

### Instrument a .NET 8 Web API

```bash
dotnet add package KamsoraAPM.Agent
```

```jsonc
// appsettings.json
"KamsoraApm": {
  "Endpoint":    "http://localhost:5080",
  "TenantId":    "<tenant uuid from the dashboard>",
  "ApiKey":      "<api key>",
  "ServiceName": "my-api"
}
```

```csharp
// Program.cs
builder.Services.AddKamsoraApm(builder.Configuration);
```

Hit any endpoint - the trace appears in the dashboard within ~1 second,
with its logs linked and the service showing up on the Service Map.

### Send from any other language (standard OTLP)

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:5080"
export OTEL_EXPORTER_OTLP_PROTOCOL="grpc"
export OTEL_EXPORTER_OTLP_HEADERS="x-kamsora-tenant=<tenant uuid>,x-kamsora-api-key=<api key>"
```

That's the whole integration - full walkthrough in [docs/ingest/otlp.md](docs/ingest/otlp.md).

### Monitor a host

The HostMonitor daemon reports a machine's CPU, memory, disk, network, and top
processes to the Collector. Pre-built, self-contained downloads (no .NET runtime
needed on the server) are attached to every
[Release](https://github.com/kamsora/KamsoraAPM/releases). Set the collector
endpoint, tenant, and API key in `appsettings.json`, then install the service:

**Linux (systemd):**

```bash
tar -xzf kamsora-apm-hostmonitor-linux-x64.tar.gz
cd kamsora-apm-hostmonitor-linux-x64
# edit appsettings.json, then:
sudo ./install-service.sh
```

**Windows (Service):** extract `kamsora-apm-hostmonitor-win-x64.zip`, edit
`appsettings.json`, then from an **elevated** PowerShell in that folder:

```powershell
powershell -ExecutionPolicy Bypass -File install-service.ps1
```

Either way it installs as an auto-start, crash-restarting service named
`KamsoraAPM.HostMonitor` and the machine appears on the **Hosts** page within
seconds. Full steps: [deploy/systemd/](deploy/systemd/README.md) (Linux),
[deploy/windows-service/](deploy/windows-service/README.md) (Windows).

---

## Production deployment

[deploy/production/](deploy/production/) contains a hardened single-host
stack: Caddy with automatic Let's Encrypt TLS (dashboard + TLS-terminated
gRPC ingest), resource limits, daily Postgres + ClickHouse backups, and a
bootstrap script. Start at
[deploy/production/DEPLOY-GUIDE.md](deploy/production/DEPLOY-GUIDE.md).

Operational notes:

- Secrets & configuration overrides - [docs/deploy/secrets.md](docs/deploy/secrets.md)
- TLS options (Caddy, nginx, bring-your-own cert) - [docs/deploy/tls.md](docs/deploy/tls.md)
- ClickHouse schema migrations run inside the Collector at startup; data
  retention per tenant is enforced by a daily partition sweep.
- The apps **refuse to start in Production with known dev-default secrets** -
  a guard rail, not a suggestion.

---

## Repository layout

```
/src
  /KamsoraAPM.Contracts        protobuf contracts (OTLP-aligned + Kamsora extensions)
  /KamsoraAPM.Agent            NuGet instrumentation client
  /KamsoraAPM.HostMonitor      host metrics daemon (systemd + Windows Service)
  /KamsoraAPM.Collector        gRPC ingestion + migrations + retention
  /KamsoraAPM.Storage          raw ADO.NET data layer (ClickHouse + PostgreSQL)
  /KamsoraAPM.Dashboard.Api    REST + JWT backend for the SPA
/web/kamsora-apm-dashboard     React 19 + Vite + TS + ECharts SPA
/deploy
  /docker                      quickstart compose + Dockerfiles
  /production                  hardened TLS stack + backups + deploy guide
  /sql                         Postgres + ClickHouse schemas
  /windows-service, /systemd   HostMonitor service installers (Windows + Linux)
/tests                         unit + integration tests
/docs                          ADRs, ingest & deploy guides
/sample-apps                   sample Web API with the Agent attached
```

---

## Status

Shipped and working today: tracing, logs (with trace correlation), metrics,
host monitoring, consumer analytics, errors, alerting, service map,
standard-OTLP ingest, multi-tenancy with RBAC/invites/audit log, gzip
transport, head sampling, automatic migrations, and per-tenant retention.

On the roadmap: alert rules over logs and metrics, anomaly detection,
trace attribute search, saved views, and continuous profiling (an
EventPipe-based v1 exists but is disabled by default while we build a safer
in-process approach).

Architectural decisions are recorded in [docs/adr/](docs/adr/).

---

## Contributing

Contributions of every size are welcome - see
[CONTRIBUTING.md](CONTRIBUTING.md) for setup and the PR process.
By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).
Security disclosures: [SECURITY.md](SECURITY.md).

---

## License

[Apache License 2.0](LICENSE) - free for personal, commercial, and SaaS use.
See [NOTICE](NOTICE) for third-party attributions.

Copyright © 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
