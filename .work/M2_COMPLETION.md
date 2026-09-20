# M2 Evidence Kernel completion record

Status: completed.
Completion date: 2026-09-20.
Successor: M3 Official War API Adapter.

This record closes the M2 milestone defined by [M2_EVIDENCE_KERNEL.md](M2_EVIDENCE_KERNEL.md).

## Delivery

M2 was implemented as four reviewable slices:

- PR #5 — governance, first migration and Source/Shard/Endpoint registry;
- PR #6 — durable queue, database-time leases, attempts and endpoint fencing;
- PR #7 — immutable payload/fetch evidence and raw-capture transaction;
- PR #8 — bounded recovery, fault handling, safety limits, observability and final verification.

## Durable model delivered

M2 owns only these PostgreSQL schemas:

~~~text
sources
ingest
evidence
~~~

The operational model includes:

~~~text
Source -> Shard -> Endpoint
Endpoint -> CollectionJob
CollectionJob -> IngestionAttempt
Endpoint -> endpoint_state / monotonic fence
IngestionAttempt -> Fetch
Fetch -> optional Payload
Fetch -> optional prior Fetch
~~~

No runtime, identity, quality or distribution schema was introduced.

## Correctness properties verified

The implementation and tests establish:

- application-generated UUIDv7 durable identifiers;
- idempotent Source/Shard/Endpoint registration;
- endpoint-scoped job enqueue idempotency, including concurrent duplicate enqueue;
- PostgreSQL `FOR UPDATE SKIP LOCKED` work claiming;
- database-time lease expiry;
- monotonic lease generation after reclaim;
- stale lease owners cannot renew/release current ownership;
- caller-generated stable AttemptId before BeginAttempt;
- idempotent BeginAttempt;
- monotonic endpoint fence tokens;
- stale fences cannot authorize or advance authority;
- only `AuthorizedNow` permits the attempt's one external exchange;
- `AlreadyAuthorized` never permits exchange replay;
- SHA-256 identity is calculated over exact supplied bytes;
- payload identity is deduplicated independently from Fetch observation identity;
- payload SHA/body-length CHECK constraints are exercised against PostgreSQL;
- one durable Fetch per AttemptId;
- no-body observations can reference a prior representation Fetch;
- repeated capture reconciles by stable AttemptId instead of duplicating evidence;
- stale or recovered responses can be preserved as `captured_late` without becoming current authority;
- expired pre-exchange work is safely requeued;
- authorized-without-Fetch work becomes `uncertain_exchange`;
- existing durable Fetch is repaired without another source exchange;
- bounded recovery is idempotent and uses PostgreSQL time/locking;
- raw payload input is bounded by configurable Evidence Kernel limits;
- recovery batch size is bounded;
- Evidence Kernel traces/metrics use low-cardinality metric dimensions and never log payload bodies.

## Final verification gate

Final PR #8 verification on the completed M2 implementation:

~~~text
Release build:       success
Compiler warnings:   0
Compiler errors:     0
Tests:               53 passed / 0 failed / 0 skipped
PostgreSQL:          18.6 Testcontainers
Docker Compose smoke: success
Contract validation: success
Dependency Review:   success
~~~

Mandatory tests do not depend on the public Internet or the official Foxhole War API.

## Deliberate M2 exclusions

M2 does not contain:

- real War API HTTP requests;
- source-specific request builders or DTO parsing;
- ETag/cache scheduling policy;
- canonical Foxhole war/map/objective state;
- public historical/query API implementation;
- S3/MinIO evidence storage;
- Redis/Kafka/RabbitMQ/NATS;
- automatic external-source retries.

The Worker therefore remains source-inert at the M2 boundary. M3 owns activation of source ingestion and orchestration around the Evidence Kernel.

## M3 handoff contract

M3 may add source-specific behavior around this sequence:

~~~text
semantic endpoint
    -> BuildRequest
    -> Claim / BeginAttempt / Fence
    -> AuthorizeExchange
    -> exactly one source exchange
    -> opaque response metadata + bytes
    -> CaptureSourceResponse
~~~

M3 must reuse the M2 queue, lease, attempt, fence, authorization, evidence and recovery semantics rather than introducing parallel source-specific correctness mechanisms.


## Post-completion hardening audit

A critical audit after the original M2 completion found concrete pre-M3 failure modes. They were addressed before source ingestion was activated.

### H1 — concurrency correctness

PR #9:

- standardized M2 row-lock order to `collection_job -> ingestion_attempt -> endpoint_state`;
- changed lease-validity decisions after lock waits to actual PostgreSQL clock time;
- added a deterministic lease-expiry/authorization race test.

### H2 — lifecycle semantics

PR #10:

- renamed endpoint raw-currentness from `last_authoritative_attempt_id` to `last_current_capture_attempt_id`;
- separated raw source-currentness from later quality/canonical acceptance;
- added explicit pre-exchange and uncertain post-authorization deferrals with durable retry eligibility;
- added ADR-0013 and a forward EF migration.

### H3 — operations and integrity

PR #12:

- deploys migrations through a one-shot EF migration bundle;
- makes readiness unhealthy when the compiled service has pending database migrations;
- checks test discovery per test project;
- revalidates exact SHA-256 at the Infrastructure persistence boundary;
- documents separate production schema/deployment and runtime database identities.

Real War API ingestion remains outside M2 and begins in M3.


## Post-hardening verification

The final H3 gate verified the hardened M2 baseline before M3 activation:

~~~text
Release build:                    success
Unit suite discovery/execution:   success
Integration suite:                success
Recovery suite:                   success
Source suite:                     success
Contract test suite:              success
Contracts workflow:               success
Dependency Review:                success
Migration bundle build:           success
Clean PostgreSQL migration job:   exit 0
Schema-aware API readiness:       success
Docker Compose smoke:             success
~~~

No real War API traffic is enabled by the hardening work.
