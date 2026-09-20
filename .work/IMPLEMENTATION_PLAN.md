# Implementation plan

Implementation proceeds in small vertical milestones with architecture/contract gates.

## M0 — architecture foundation

Deliver:

- .work authority;
- ADRs;
- transport model;
- source semantics;
- initial OpenAPI/AsyncAPI/stdio contracts;
- ecosystem research;
- contract CI design.

No runtime source ingestion yet.

## M1 — repository bootstrap

Status: completed.

Detailed normative plan: [M1_REPOSITORY_BOOTSTRAP.md](M1_REPOSITORY_BOOTSTRAP.md).

Create the reproducible .NET 10 repository foundation, explicit project-reference graph, PostgreSQL/Testcontainers bootstrap, common hosting/OpenTelemetry defaults, API health surface, inert Worker/CLI processes, Docker/Compose bootstrap, MTP/xUnit v3 testing, contract tooling and supply-chain CI.

M1 must not implement source polling, War API DTOs, evidence tables or canonical domain state.

## M2 — evidence kernel

Status: completed 2026-09-20. Completion record: [M2_COMPLETION.md](M2_COMPLETION.md).

Detailed normative plan: [M2_EVIDENCE_KERNEL.md](M2_EVIDENCE_KERNEL.md).

Implement source/shard/endpoint registry, durable queue, database-time leases, attempt lifecycle, endpoint fencing, one-way exchange authorization, immutable fetch evidence, SHA-256 payload identity, PostgreSQL-inline bytea raw storage, raw-capture transactions and crash/unknown-outcome recovery.

External object storage is deliberately deferred until M4 measurements justify it.

## M3 — official War API adapter

Status: next milestone.

Implement all documented endpoints:

- war
- maps
- warReport
- static
- dynamic/public

Add conditional GET, cache eligibility, tolerant DTOs, unknown field/code preservation and structural fingerprints.

No hidden retries.

## M4 — source measurement

Run bounded 48–72 hour measurement.

Publish measured collection-profile@1.

## M5 — canonical war/region/report model

Implement:

- war/shard identity;
- lifecycle/time anchors;
- regions;
- war reports;
- coverage.

## M6 — maps, taxonomy and quality

Implement:

- static/dynamic observations;
- versioned icon/flag taxonomy;
- anomaly rules;
- issue-92 regression behavior.

## M7 — objective identity

Implement within-war objective instances, match runs/candidates/decisions and ambiguity.

Cross-war identity can remain optional until evidence justifies it.

## M8 — state intervals and observed changes

Implement:

- state intervals;
- changeSeq ordering;
- uncertainty windows;
- reconstruction boundary;
- state-at-time query.

## M9 — canonical HTTP API v1

Implement public read API, RFC 9457 errors, ETag, CORS exposure, cursor pagination and query bounds.

Generate and diff OpenAPI.

## M10 — stdio RPC v1 and CLI

Implement:

- human CLI;
- machine JSON/NDJSON output;
- foxdata rpc --stdio;
- initialize/shutdown;
- core query methods;
- subscriptions with durable cursor recovery.

## M11 — War API compatibility facade

Implement shard-explicit source-shaped paths and provenance headers.

## M12 — change distribution

Implement durable pull feed, SSE, AsyncAPI contract and CloudEvents payloads.

## M13 — webhooks

Implement Standard Webhooks-compatible signing, subscriptions, SSRF protections, retry audit, dead-letter and replay.

## M14 — exports

Implement async operation resources, NDJSON/CSV/Parquet and immutable manifests.

## M15 — SDKs/helpers

Release first-wave TypeScript, Python, C# and Rust transport SDKs plus separate helper packages.

## M16 — optional reference adapters

Evaluate assets/reference sources independently from runtime-war availability.

## M17 — hardening

- contract compatibility gates;
- load tests;
- security tests;
- restore drills;
- SLOs;
- documentation portal;
- API-key quotas where needed.

## Deferred transport gate

Evaluate gRPC/UDS/Named Pipes only after stdio and HTTP usage reveal a concrete need such as throughput, strongly typed streaming or local daemon reuse.
