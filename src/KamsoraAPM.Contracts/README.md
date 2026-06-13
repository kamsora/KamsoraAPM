# KamsoraAPM.Contracts

OTLP-compatible Protobuf + gRPC contracts shared by the **[KamsoraAPM](https://github.com/kamsora/KamsoraAPM)** Agent, HostMonitor, Collector, and Dashboard API.

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://github.com/kamsora/KamsoraAPM/blob/main/LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)

## What's in the box

| Proto package | Purpose |
|---|---|
| `kamsora.common.v1` | `AnyValue`, `KeyValue`, `Resource`, `InstrumentationScope`, `TenantContext` — shared primitives |
| `kamsora.trace.v1` | `Span`, `ResourceSpans`, `Status`, `Event` — distributed-tracing surface (OTLP-shaped) |
| `kamsora.metrics.v1` | `ResourceMetrics`, `Metric`, `Gauge`, `Sum`, `Histogram` — metrics surface |
| `kamsora.logs.v1` | `ResourceLogs`, `LogRecord` — log records |
| `kamsora.host.v1` | `HostSnapshot`, `CpuSample`, `MemorySample`, `DiskSample`, `NetworkSample`, `ProcessSample` — host telemetry |
| `kamsora.collector.v1` | `TraceService`, `MetricsService`, `LogsService`, `HostService`, `IngestControl` — gRPC ingest surface |

## Install

```powershell
dotnet add package KamsoraAPM.Contracts
```

Most users don't reference this package directly — it's a transitive dependency of [`KamsoraAPM.Agent`](https://www.nuget.org/packages/KamsoraAPM.Agent). Reference it explicitly only if you're building a custom Collector, a custom Agent, or a polyglot SDK in another language.

## License

Apache 2.0. See the [LICENSE](https://github.com/kamsora/KamsoraAPM/blob/main/LICENSE) file.

## Links

- **Source**: https://github.com/kamsora/KamsoraAPM
- **Issues**: https://github.com/kamsora/KamsoraAPM/issues
