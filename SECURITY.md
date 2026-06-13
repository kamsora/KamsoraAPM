# Security Policy

## Supported Versions

KamsoraAPM is currently in pre-release. Until our first GA release we will
provide security fixes for:

| Version           | Supported          |
| ----------------- | ------------------ |
| `main` (HEAD)     | :white_check_mark: |
| Latest pre-release| :white_check_mark: |
| Older pre-releases| :x:                |

Once we reach 1.0 we will publish a versioned support matrix.

## Reporting a Vulnerability

**Please do not file public GitHub issues for security vulnerabilities.**

Instead, email **security@kamsora.com** with:

- A description of the vulnerability and its impact.
- A minimal proof-of-concept (or steps to reproduce).
- Your contact details and any disclosure timeline preferences.

You will receive an acknowledgement within **3 business days**, a
triage assessment within **7 business days**, and we aim to ship a fix
within **30 days** for high-severity issues.

We do not currently run a bug-bounty program, but we credit reporters in
release notes unless they request otherwise.

## Coordinated disclosure

We follow the principles of coordinated disclosure. We will work with you
on a disclosure timeline before a public advisory is published. Please
give us a reasonable window to investigate and ship a fix.

## Scope

In-scope:

- Code under `src/`, `web/`, `deploy/`.
- Default Docker images shipped from this repository.
- Default configuration files committed to this repository.

Out of scope:

- Self-hosted deployments where the operator has materially diverged
  from the default configuration.
- Third-party plug-ins or forks.
- Issues affecting only end-of-life pre-releases.

## Hardening guidance

For production hardening — TLS termination, secrets management, and
network segmentation — see:

- [docs/deploy/secrets.md](docs/deploy/secrets.md) — overriding the
  shipped dev defaults with real secrets.
- [docs/deploy/tls.md](docs/deploy/tls.md) — TLS options (Caddy,
  nginx, bring-your-own certificate).
- [deploy/production/DEPLOY-GUIDE.md](deploy/production/DEPLOY-GUIDE.md) —
  the hardened single-host production stack.
