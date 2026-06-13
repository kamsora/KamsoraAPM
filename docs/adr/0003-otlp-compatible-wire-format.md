# ADR-0003: Wire format - OTLP-compatible

- **Status**: Accepted
- **Date**: 2026-05-24
- **Component(s)**: `KamsoraAPM.Contracts`, `KamsoraAPM.Agent`, `KamsoraAPM.Collector`

## Context

The Agent sends telemetry to the Collector over gRPC. The choice of wire
format determines:

- Interoperability with the wider observability ecosystem.
- How much code we maintain inside `KamsoraAPM.Contracts`.
- How "lock-in" the project feels to potential adopters.

Three options:

1. **Kamsora-native protobuf.** A bespoke schema tailored to .NET specifics
   (e.g. dedicated ThreadPool / GC counter messages). Smaller wire footprint,
   total schema control, but isolates KamsoraAPM from the OpenTelemetry
   ecosystem.
2. **OTLP only.** Adopt the OpenTelemetry Protocol verbatim. Any
   OTel-instrumented service in any language can send to KamsoraAPM with
   only an endpoint change.
3. **Both (dual ingestion).** Collector exposes OTLP services and a Kamsora-
   native service side by side. Max flexibility, but ~30 % more code in
   `Contracts` and `Collector`.

## Decision

The wire format is **OTLP-compatible**. Specifically:

- The message shapes in `kamsora.{common,trace,metrics,logs}.v1` mirror the
  corresponding OpenTelemetry messages field-for-field. They are declared in
  the Kamsora package namespace so the contracts assembly is self-contained
  and the codegen has no external `.proto` dependencies at M0/M1.
- In **M2** the Collector will additionally accept the upstream OTLP service
  descriptors (`opentelemetry.proto.collector.{trace,metrics,logs}.v1.*`),
  using vendored OTLP `.proto` files. Wire-level binary compatibility means
  this shim is essentially a thin adapter.
- KamsoraAPM-specific signals that OTLP does not model - host snapshots
  with per-PID attribution - live in `kamsora.host.v1` and remain a
  Kamsora extension.

## Consequences

### Positive

- Existing OpenTelemetry SDKs (Java, Go, Python, Node, Rust …) can target
  the KamsoraAPM Collector with no Kamsora-specific client code.
- Migration *off* KamsoraAPM is also trivial - telemetry can be tee'd to a
  vanilla OTLP backend (Tempo, Datadog, etc.) at the same time.
- The contracts assembly remains stable: future OTLP additions are *added*
  as fields, never breaking.

### Negative / trade-offs

- Slightly larger wire format than a hand-tuned Kamsora-native schema -
  OTLP carries some fields we don't use yet (e.g. `trace_state`). Measured
  cost: < 5 % over a pure Kamsora schema, well within the 2 ms / 2 % budget.
- We must stay vigilant about OTLP version changes; an ADR is required when
  vendoring a new OTLP `.proto` version.

### Neutral

- The Agent's payload structure is dictated by OTLP semantics. Internal
  enrichment (e.g. ThreadPool counters) is carried as OTLP attributes
  with the `kamsora.` prefix.

## Alternatives considered

- **Kamsora-native only** - rejected: insufficient ecosystem reach for a
  project that wants to be "world's best."
- **Dual ingestion at M0** - rejected as premature; we will revisit in M2
  when adding the OTLP-shim service.

## References

- OTLP specification:
  <https://github.com/open-telemetry/opentelemetry-proto>
- OpenTelemetry semantic conventions:
  <https://opentelemetry.io/docs/specs/semconv/>
