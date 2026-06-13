# ADR-0001: License — Apache 2.0

- **Status**: Accepted
- **Date**: 2026-05-24
- **Component(s)**: project-wide

## Context

KamsoraAPM is being released as an open-source project. The project's
stated goals are:

1. Be **free for anyone to use** — personal, commercial, or hosted.
2. Be enterprise-friendly so commercial users can adopt without legal review
   becoming a blocker.
3. Provide some defence against patent litigation from contributors.
4. Stay compatible with the Apache 2.0 licenses of our principal upstream
   dependencies (OpenTelemetry, gRPC, ClickHouse).

Four licenses were on the table:

- **Apache 2.0** — permissive, includes a patent grant.
- **MIT** — permissive, no patent grant.
- **AGPL-3.0** — copyleft, requires SaaS resellers to open-source their
  modifications.
- **BSL → Apache 2.0** — Business Source License with a delayed
  Apache conversion (Sentry / MariaDB model).

## Decision

KamsoraAPM is licensed under the **Apache License, Version 2.0**.
The full license text is committed to the repository root as `LICENSE`,
third-party attributions live in `NOTICE`.

## Consequences

### Positive

- Single, well-understood license — no friction for enterprise adoption.
- Explicit patent grant from contributors via Section 3 — important for an
  observability product where adjacent IP is often litigated.
- Compatible with the Apache 2.0 OpenTelemetry ecosystem we are integrating
  with at the wire-format level.
- Permits relicensing of contributions inside derivative works under the
  same terms.

### Negative / trade-offs

- Does **not** prevent cloud vendors from running KamsoraAPM as a hosted
  service without contributing back. If this becomes a problem we can move
  *new* components to a more restrictive license, but the existing
  Apache 2.0 grant is irrevocable for code already released under it.

### Neutral

- We will require a Contributor License Agreement only if/when needed for
  organisational relicensing; for now, the Apache 2.0 inbound grant
  recorded in `CONTRIBUTING.md` is sufficient.

## Alternatives considered

- **MIT** — rejected because it lacks an explicit patent grant; weaker
  protection against patent trolls.
- **AGPL-3.0** — rejected because it makes commercial adoption significantly
  harder and several major foundations require Apache-style licensing.
- **BSL → Apache 2.0** — rejected because it complicates the "free for
  everyone" positioning of the project. We can re-evaluate if a hosted
  KamsoraAPM service is ever offered.

## References

- <https://www.apache.org/licenses/LICENSE-2.0>
- OpenTelemetry license: <https://github.com/open-telemetry/opentelemetry-specification/blob/main/LICENSE>
