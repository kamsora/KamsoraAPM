# Sending data from any OpenTelemetry SDK (OTLP)

KamsoraAPM's Collector speaks **standard OTLP/gRPC** for traces, metrics,
and logs. Any OpenTelemetry SDK — Python, Node, Java, Go, Ruby, PHP, .NET —
can export to it directly. No KamsoraAPM-specific library required.

## Configuration (any language)

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="http://<collector-host>:5080"
export OTEL_EXPORTER_OTLP_PROTOCOL="grpc"
export OTEL_EXPORTER_OTLP_HEADERS="x-kamsora-tenant=<tenant-uuid>,x-kamsora-api-key=<api-key>"
export OTEL_SERVICE_NAME="my-python-service"
```

Get the tenant UUID and API key from the dashboard's **API Keys** page.

## Python example

```python
# pip install opentelemetry-distro opentelemetry-exporter-otlp
# opentelemetry-instrument python app.py     (env vars above set)
```

## Node example

```bash
npm install @opentelemetry/auto-instrumentations-node
node --require @opentelemetry/auto-instrumentations-node/register app.js
```

## Java example

```bash
java -javaagent:opentelemetry-javaagent.jar -jar app.jar
```

## What lands where

| Signal | Dashboard page |
|---|---|
| Traces | Traces / Services / Overview / Consumers / Errors |
| Logs (with trace context) | Logs page + linked on the trace view |
| Metrics (gauge / sum / histogram) | Metrics page |

Exponential histograms and summaries are accepted on the wire but their
data points are currently skipped (only gauge / sum / explicit-bounds
histogram points are stored).

## .NET apps

.NET apps can use plain OTLP too, but the **KamsoraAPM.Agent** NuGet is
recommended instead — it adds consumer analytics, host correlation, and
deeper SQL capture on top of the same pipeline.
