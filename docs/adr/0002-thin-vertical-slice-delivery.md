# ADR-0002: Delivery model — thin vertical slices per milestone

- **Status**: Accepted
- **Date**: 2026-05-24
- **Component(s)**: project-wide / process

## Context

KamsoraAPM has four independently deployable components (Agent, HostMonitor,
Collector, Dashboard) and two databases (ClickHouse, PostgreSQL). With this
many moving parts there are two natural ways to sequence development:

1. **Component-first / depth-first.** Build the Agent to completion, then
   the Collector, then the Dashboard. Each component is fully done before
   the next starts. Risk: nothing is end-to-end demonstrable for months;
   integration problems surface very late.
2. **Vertical-slice / breadth-first.** Each milestone delivers a thin path
   through all components that produces a real, runnable demo. Subsequent
   milestones widen that path until the product is feature-complete.

The project's stated quality bar ("world's best, production-grade") and the
explicit ask for a runnable docker-compose stack point to option (2).

## Decision

We deliver KamsoraAPM as a sequence of milestones (`M0`, `M1`, … `M6`).
**Each milestone must end with a runnable, demonstrable artifact** — typically
a docker-compose stack and/or a sample app — and must not regress demos
shipped in earlier milestones.

The initial milestones are:

- **M0** Repo skeleton, license, CI, docker-compose for ClickHouse +
  Postgres, ADRs. *(this milestone)*
- **M1** Thin vertical slice: Agent captures one HTTP request → gRPC to
  Collector → persisted to ClickHouse → visible in a single Dashboard page.
- **M2** Agent feature-complete: middleware, EF/Dapper/HttpClient
  instrumentation, exception capture, ThreadPool/GC metrics, OTLP payload.
- **M3** HostMonitor for Linux + Windows.
- **M4** Dashboard.Api + React SPA: Unified Health Overview, Trace Explorer.
- **M5** Infrastructure Topology + Alerting Rules Engine + Webhooks.
- **M6** Hardening: load tests prove 25 k RPS + < 2 ms overhead; bare-metal
  installers; production docs.

## Consequences

### Positive

- Integration risk is amortised across every milestone, not concentrated
  at the end.
- A working demo from M1 onward makes contributor onboarding much easier
  and lets us collect feedback early.
- Forces us to make the gRPC contract real and stable from M1, rather than
  inventing it last.

### Negative / trade-offs

- Some components ship in a partially-featured state for several
  milestones. We must guard against the temptation to skip hardening
  ("we'll fix it later") — every milestone explicitly enumerates the
  invariants it must uphold.
- The M1 slice has to design for multi-tenancy from the start
  (see ADR-0004), which makes M1 less "thin" than it sounds.

### Neutral

- Each PR description and ADR must call out which milestone(s) it
  contributes to.

## Alternatives considered

- **Component-first** — rejected for the integration-risk reasons above.
- **Big-bang release** — never seriously considered for a project of this
  size.

## References

- See `README.md` for the live milestone list.
