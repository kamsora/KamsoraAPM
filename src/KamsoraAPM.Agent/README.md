# KamsoraAPM.Agent

The in-process .NET agent for **[KamsoraAPM](https://github.com/kamsora/KamsoraAPM)** — a free, self-hostable APM and infrastructure observability platform purpose-built for .NET Core Web APIs.

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://github.com/kamsora/KamsoraAPM/blob/main/LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)

## What it does

- Captures HTTP server activities (ASP.NET Core), outbound HTTP calls (`HttpClient`), and database calls (SqlClient + Npgsql + MySqlConnector + EF Core) automatically.
- Forwards spans to your **KamsoraAPM Collector** over gRPC, using an **OTLP-compatible wire format**.
- Adds **less than 2 ms** of latency per HTTP request and **less than 2 % CPU** under load.
- Multi-tenant from day one — every span carries a `tenant_id` validated server-side.

## Install

```powershell
dotnet add package KamsoraAPM.Agent
```

## Wire it up

In your `Program.cs`:

```csharp
builder.Services.AddKamsoraApm(o =>
{
    o.CollectorEndpoint = "http://your-collector-host:5080";
    o.TenantId          = "<your-tenant-uuid>";
    o.ApiKey            = "<your-ingest-api-key>";
    o.ServiceName       = "my-service-name";
});
```

Or via `appsettings.json`:

```json
{
  "KamsoraApm": {
    "Agent": {
      "CollectorEndpoint": "http://your-collector-host:5080",
      "TenantId":          "<your-tenant-uuid>",
      "ApiKey":            "<your-ingest-api-key>",
      "ServiceName":       "my-service-name"
    }
  }
}
```

The tenant UUID and API key are minted from the KamsoraAPM dashboard's **API Keys** page (or — for the very first tenant — printed once in the dashboard's startup logs).

## What gets captured automatically

| Source | Captured tags |
|---|---|
| ASP.NET Core HTTP server | `http.request.method`, `http.route`, `url.path`, `http.response.status_code`, `user_agent.original`, `server.address` |
| `HttpClient` outbound | `http.request.method`, `url.full`, `http.response.status_code` |
| `Microsoft.Data.SqlClient` / `System.Data.SqlClient` | `db.system=mssql`, `db.statement`, `db.connection_string` |
| Npgsql 6+ (PostgreSQL) | `db.system=postgresql`, `db.statement` (auto via Activity source) |
| MySqlConnector | `db.system=mysql`, `db.statement` |
| EF Core | One span per query, parent linked to surrounding HTTP request |
| Any custom `ActivitySource` whose name starts with `Kamsora.` | Forwarded verbatim |

## Architecture

This package is the agent half of the KamsoraAPM stack. The full stack is:

- **KamsoraAPM.Agent** *(this package)* — in-process .NET library
- **KamsoraAPM.HostMonitor** — host-level CPU/RAM/disk/network/process daemon (Windows Service or systemd)
- **KamsoraAPM.Collector** — gRPC ingest, multi-tenant auth, writes to ClickHouse
- **KamsoraAPM Dashboard** — React SPA showing live traces, services, hosts, top processes

Everything except this NuGet package is self-hosted from the [KamsoraAPM repo](https://github.com/kamsora/KamsoraAPM).

## License

Apache 2.0. See the [LICENSE](https://github.com/kamsora/KamsoraAPM/blob/main/LICENSE) file.

## Links

- **Source**: https://github.com/kamsora/KamsoraAPM
- **Issues / feedback**: https://github.com/kamsora/KamsoraAPM/issues
- **Docs**: https://github.com/kamsora/KamsoraAPM#readme
