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

Status: completed 2026-09-27. Completion record: [M5_COMPLETION.md](M5_COMPLETION.md).

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

- M5-A contract foundation — complete;
- M5-B persistence foundation — complete;
- M5-C normalization kernel — complete;
- M5-D war normalization — complete;
- M5-E region discovery/membership — complete;
- M5-F war-report normalization — complete;
- M5-G coverage/recovery verification — complete;
- M5-H completion gate — complete.

## M6 — maps, taxonomy and quality

Status: in progress. M6-A through M6-F2 complete through PR #50; runtime activation of calibrated policy@2 remains deferred to M6-G.

Detailed normative plan: [M6_MAPS_TAXONOMY_QUALITY.md](M6_MAPS_TAXONOMY_QUALITY.md).
Execution sequence for the remaining slices: [M6_EXECUTION_PLAN.md](M6_EXECUTION_PLAN.md).
Research evidence: [research/M6_MAP_QUALITY_2026-09.md](research/M6_MAP_QUALITY_2026-09.md).

M6 converts durable static/dynamic War API representations into normalized source-occurrence snapshots, applies versioned taxonomy and quality policy, and materializes only quality-accepted war-scoped map observations.

M6 deliberately stops before objective identity and state-change inference.

Implementation slices:

- M6-A contract, context and persistence foundation — complete;
- M6-B static map normalization — complete;
- M6-C dynamic map normalization — complete;
- M6-D versioned taxonomy — complete;
- M6-E quality kernel and accepted-observation transaction — complete through E3, merged in PR #48;
- M6-F1 golden fixture catalog + deterministic offline feature extraction — merged in PR #49; M6-F2 immutable calibrated anomaly policy@2 + fail-closed offline replay — complete in PR #50, with runtime activation deferred to M6-G so structural-only @1 remains historically reproducible;
- M6-G1 source-neutral coverage capability-plan refactor — in progress; M6-G2+ static/dynamic coverage, local recovery, 304 continuity and versioned reprocessing — pending;
- M6-H deterministic completion gate and M6 completion record — pending.

Mandatory boundaries:

- source array occurrence is not ObjectiveId;
- duplicates are preserved;
- static and dynamic remain independent snapshot streams;
- unknown icon/team/flag values remain representable;
- quality rejection/quarantine never destroys source evidence;
- 304 validation does not duplicate normalized source snapshots;
- M6 reuses M5 war/WarRegion provenance and cannot weaken M5-H;
- M7 owns within-war objective matching.

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
