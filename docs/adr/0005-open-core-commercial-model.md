# ADR-0005: Open-core commercial model

- **Status**: Accepted
- **Date**: 2026-06-13
- **Component(s)**: project-wide, business model

## Context

KamsoraAPM is heading toward commercialization with two revenue streams in
mind:

1. **Hosted SaaS (Kamsora Cloud)** — users sign up on our portal; a free
   tier covers 1 service + 1 host with 14-day retention; paid tiers unlock
   more services/hosts and longer retention.
2. **Enterprise self-host** — large organizations running KamsoraAPM
   on-prem with needs beyond the core product (SSO/SAML, advanced RBAC,
   compliance reporting, priority support, SLAs).

ADR-0001 licensed the project Apache 2.0, which permits unrestricted
commercial use and self-hosting. A "commercial use requires a license"
model would conflict with that and would require relicensing to
source-available terms (FSL/BSL) or AGPL dual-licensing.

The decision had to be made **before** the first public release: today
Kamsora is the sole copyright holder and can relicense freely; after
external contributions arrive, any license change needs contributor
agreements.

Models considered:

- **Open-core** (Grafana, SigNoz): core stays Apache 2.0 forever; revenue
  from hosting convenience and proprietary enterprise add-ons.
- **FSL/BSL source-available** (Sentry, MariaDB): self-host free, competing
  services prohibited, delayed conversion to Apache. Enforces "enterprises
  must pay" but forfeits the open-source label and some community trust.
- **AGPL + commercial dual license** (Grafana post-2021): copyleft pressure
  drives enterprises to buy; slows grassroots adoption.

## Decision

KamsoraAPM adopts the **open-core** model:

1. The **core platform stays Apache 2.0** — everything in this repository
   today (Agent, HostMonitor, Collector, Dashboard, all four telemetry
   pillars, alerting, multi-tenancy). Self-hosting the core is free and
   unlimited, for everyone, forever. ADR-0001 stands.
2. **Kamsora Cloud** (hosted SaaS) monetizes convenience: free tier
   (1 service, 1 host, 14-day retention), paid tiers for more. Billing,
   signup portal, and quota plumbing specific to the cloud offering are
   proprietary.
3. **Enterprise features** ship later as a separate proprietary layer
   (an `ee/` directory or private repository): SSO/SAML, advanced RBAC,
   audit exports, priority support contracts. The boundary rule: a feature
   belongs in `ee/` only if its primary buyer is an organization with
   procurement, never if its absence cripples the core product.
4. **Trademark** ("Kamsora", "KamsoraAPM") is registered and enforced
   separately from the code license — forks may use the code, not the brand.
5. Outside contributions to the core are accepted under a **DCO**
   (Developer Certificate of Origin); contributions never land in `ee/`
   without explicit agreement.

## Consequences

### Positive

- Maximum adoption: "free unlimited self-host" is the strongest possible
  growth loop for a developer tool with zero marketing budget.
- No relicensing cliff: the open-source promise is simple and permanent,
  which builds the trust a young observability vendor needs.
- The multi-tenant architecture (ADR-0004) was built for exactly this —
  the SaaS portal is plan/quota/billing plumbing on top of what exists.

### Negative / risks

- A hyperscaler could offer hosted KamsoraAPM without paying us. Accepted:
  at this stage the bottleneck is adoption, not competitors; revisit only
  if the project reaches that scale (a good problem).
- Discipline required at the `ee/` boundary — moving too much value into
  proprietary code erodes community trust (the "open-core bait" failure
  mode).
- Two codebases to maintain once `ee/` exists.

### Follow-ups

- SaaS milestone: tenant `plan` column + quota enforcement (max services /
  hosts, retention by plan), public signup, Stripe billing, usage page.
- CONTRIBUTING.md: add DCO sign-off requirement before external PRs arrive.
- Trademark registration for "Kamsora" / "KamsoraAPM".
