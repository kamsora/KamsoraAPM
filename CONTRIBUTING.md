# Contributing to KamsoraAPM

Thank you for considering a contribution! KamsoraAPM is an Apache-2.0
licensed open-source project and we welcome everything from a single-line
typo fix to a brand-new instrumentation provider.

This document explains how to set up your environment, our coding
standards, and the process for proposing and shipping changes.

---

## Quick links

- [Code of Conduct](CODE_OF_CONDUCT.md)
- [Security policy](SECURITY.md)
- [Architecture Decision Records](docs/adr/)
- Issue tracker: GitHub Issues
- Discussions: GitHub Discussions

---

## Prerequisites

| Tool       | Version | Why |
| ---------- | ------- | --- |
| .NET SDK   | 8.0.x   | All `src/` projects target `net8.0` |
| Node       | 20.x +  | Dashboard SPA build |
| pnpm       | 9.x +   | Web workspace package manager |
| Docker     | 24.x +  | Local ClickHouse + Postgres |
| Git        | 2.40 +  | Repo |

Optional but recommended:

- **`dotnet-format`** - runs as part of pre-commit.
- **`buf`** or **`protoc`** - to lint Protobuf changes locally.

---

## Cloning and bootstrapping

```bash
git clone https://github.com/kamsora/KamsoraAPM.git
cd KamsoraAPM

# .NET solution
dotnet restore
dotnet build

# Web dashboard
pnpm --filter "./web/*" install
pnpm --filter "./web/*" build

# Bring up dependencies for integration tests
docker compose -f deploy/docker/docker-compose.deps.yml up -d
```

Run the full test suite:

```bash
dotnet test
pnpm --filter "./web/*" test
```

---

## Coding standards

### General

- **Be conservative with dependencies.** Every new NuGet/npm dependency
  ships with our binary - prefer the BCL when possible.
- **Treat warnings as errors.** This is enforced in
  [Directory.Build.props](Directory.Build.props).
- **Cancellation tokens** are required on every async public method.
- **Structured logging** only - use the `ILogger<T>` with templates,
  never string interpolation.

### .NET - performance-critical paths

The `KamsoraAPM.Agent` and the Collector's ingestion path are
**hot paths**. They must obey:

1. **No allocations** in steady state beyond pooled buffers.
   Use `ArrayPool`, `Span<T>`, `Memory<T>`.
2. **No sync-over-async.** All I/O is awaited; cancellation flows down.
3. **No EF Core, no LINQ-to-SQL, no heavy ORMs.** Raw ADO.NET +
   `Npgsql` / `ClickHouse.Client` only on the ingestion path.
4. **No blocking sends** from the Agent. Telemetry enqueues to a
   bounded `Channel<T>`; a background flusher drains it.

### Protobuf

- We accept and emit **OTLP** as the wire format. Kamsora-specific
  fields go into the OTLP `attributes` map with the `kamsora.` prefix.
- Breaking changes to `.proto` files require an ADR.

### React / TypeScript

- TypeScript strict mode is mandatory.
- Components are functional + hooks. No class components.
- Co-locate tests as `*.test.tsx`.

---

## Branching, commits, PRs

1. Fork the repo and create a feature branch from `main`:
   `feat/agent-thread-pool-metrics`, `fix/collector-flush-deadlock`,
   `docs/adr-otlp-extensions`.
2. Keep PRs small and focused. If a change requires more than ~600
   lines of diff, split it.
3. Commit messages follow **Conventional Commits**
   (`feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `perf:`, `test:`).
4. Every PR must:
   - Pass `dotnet build` with no warnings.
   - Pass `dotnet test` and `pnpm test`.
   - Update or add tests covering the change.
   - Update relevant docs / ADRs.

### What requires an ADR?

- New or removed components.
- Wire-format changes (Protobuf, REST contracts).
- Storage schema changes that are not strictly additive.
- New mandatory dependencies in the hot path.

Use the template in [docs/adr/template.md](docs/adr/template.md).

---

## Reporting bugs

When opening an issue, please include:

- KamsoraAPM version (or commit SHA).
- .NET runtime version (`dotnet --info`).
- Host OS and architecture.
- Minimal reproduction (a sample project is ideal).
- Logs from Agent + Collector with `LogLevel=Debug` if possible.

For **security** issues, do **not** open a public issue - follow
[SECURITY.md](SECURITY.md) instead.

---

## Licensing of contributions

KamsoraAPM follows an **open-core** model (see
[ADR-0005](docs/adr/0005-open-core-commercial-model.md)): the core in this
repository is and stays Apache 2.0. Contributions to the core are licensed
under the [Apache License 2.0](LICENSE).

### Developer Certificate of Origin (DCO)

All contributions must be signed off under the
[Developer Certificate of Origin](https://developercertificate.org/). The
sign-off certifies that you wrote the patch or otherwise have the right to
submit it under the open-source license. Add a `Signed-off-by` line to every
commit:

```
Signed-off-by: Your Name <you@example.com>
```

`git commit -s` adds this automatically. Pull requests whose commits are not
signed off cannot be merged.

Welcome aboard!
