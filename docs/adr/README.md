# Architecture Decision Records

This directory contains Architecture Decision Records (ADRs) for KamsoraAPM,
in the Michael Nygard style.

## Index

| #     | Title                                                                                       | Status   | Date       |
| ----- | ------------------------------------------------------------------------------------------- | -------- | ---------- |
| 0001  | [License: Apache 2.0](0001-license-apache-2.0.md)                                           | Accepted | 2026-05-24 |
| 0002  | [Delivery model: thin vertical slices per milestone](0002-thin-vertical-slice-delivery.md)  | Accepted | 2026-05-24 |
| 0003  | [Wire format: OTLP-compatible](0003-otlp-compatible-wire-format.md)                         | Accepted | 2026-05-24 |
| 0004  | [Multi-tenancy from day one](0004-multi-tenancy-from-day-one.md)                            | Accepted | 2026-05-24 |
| 0005  | [Open-core commercial model](0005-open-core-commercial-model.md)                            | Accepted | 2026-06-13 |

New ADRs use the [template](template.md) and increment the number monotonically.
ADRs are append-only — to change a previous decision, write a new ADR that
**supersedes** the old one, and update the old one's status.
