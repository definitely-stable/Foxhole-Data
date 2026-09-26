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

Status: completed 2026-09-20. Completion record: [M3_COMPLETION.md](M3_COMPLETION.md).

Detailed normative plan: [M3_OFFICIAL_WAR_API_ADAPTER.md](M3_OFFICIAL_WAR_API_ADAPTER.md).

Implement all documented endpoint families:

- war;
- maps;
- warReport;
- static;
- dynamic/public.

M3 additionally owns:

- fixed shard-root request construction;
- conditional GET and body-bearing representation lineage;
- explicit HTTP cache/retry eligibility;
- a poll-state projection separate from M2 fence authority;
- bounded HTTP response streaming/decoding;
- tolerant source DTOs and open upstream values;
- structural fingerprinting;
- versioned source parse runs;
- map endpoint discovery;
- crash-safe successor planning;
- deterministic request spreading;
- production Worker recovery/planner/executor orchestration.

No hidden application retry, hedging or redirect is permitted.

M3 still does not implement canonical war/map/objective state or quality acceptance.

Implementation slices:

- M3-A specification/contracts/fixtures;
- M3-B poll-state and parse-run persistence;
- M3-C parser and structural fingerprint;
- M3-D HTTP transport/cache policy;
- M3-E durable Worker orchestration;
- M3-F controlled activation and completion.

## M4 — source measurement

Status: completed 2026-09-24. Completion record: [M4_COMPLETION.md](M4_COMPLETION.md).

Detailed normative plan: [M4_SOURCE_MEASUREMENT.md](M4_SOURCE_MEASUREMENT.md).
Measured policy result: [M4_COLLECTION_POLICY.md](M4_COLLECTION_POLICY.md).
Operational runbook: [M4_RUNBOOK.md](M4_RUNBOOK.md).

M4 measured Official War API source behaviour over 48.169 active hours and delivered:

- reproducible analysis over M2/M3 durable evidence;
- cache/ETag/200-vs-304 measurement;
- source representation-change and map-version-gap measurement;
- payload/latency/error distributions;
- request burst and scheduler/executor capacity measurement;
- PostgreSQL evidence-growth measurement;
- an 8-hour bounded deterministic high-resolution Live-1 probe;
- an optional measured `collection-profile@1` recommended preset;
- operator-selectable `collection-policy@1` with bootstrap/recommended/custom modes;
- a mandatory safety envelope that custom cadence cannot bypass;
- retention of serial executor concurrency = 1 from measured capacity;
- retention of inline PostgreSQL raw evidence through ADR-0017.

M4 does not implement canonical Foxhole state or quality acceptance.

Implementation slices:

- M4-A specification/contracts — complete;
- M4-B collection-profile abstraction with behaviour-preserving bootstrap values — complete;
- M4-C reproducible measurement analyzer — complete;
- M4-D bounded deterministic probe mode — complete;
- M4-E controlled 48-hour live campaign — complete;
- M4-F measured policy publication, storage/executor decisions and completion — complete.

## M5 — canonical war/region/report model

Status: in progress.

Detailed normative plan: [M5_CANONICAL_WAR_REGION_REPORT.md](M5_CANONICAL_WAR_REGION_REPORT.md).

Implement:

- war/shard identity;
- lifecycle/time anchors;
- regions and within-war source-region membership;
- versioned normalization-run provenance;
- immutable war observations;
- immutable war-report observations;
- coverage and local evidence reprocessing.

Implementation slices:

- M5-A contract foundation — in progress;
- M5-B persistence foundation — in progress;
- M5-C normalization kernel — pending;
- M5-D war normalization — pending;
- M5-E region discovery/membership — pending;
- M5-F war-report normalization — pending;
- M5-G coverage/recovery verification — pending;
- M5-H completion gate — pending.

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
