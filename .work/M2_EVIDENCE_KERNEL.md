# M2 — Evidence Kernel

Status: completed 2026-09-20. Implemented by PRs #5, #6, #7 and #8. Successor: M3 official War API adapter.
Prerequisite: M1 Repository Bootstrap completed.
Successor: M3 official War API adapter.

M2 creates the durable evidence and concurrency kernel that every later collector, parser, quality gate, canonical model and historical query depends on.

M2 is intentionally source-semantic-neutral. It can persist and audit an opaque source response without knowing whether the bytes describe a war, map, report, item or any other Foxhole concept.

The milestone is complete only when the system can safely coordinate multiple workers, authorize at most one source exchange per durable attempt, survive process/database ambiguity, retain exact raw bytes, detect stale workers through fencing, and recover expired work without replaying the same attempt.

## 1. Why M2 exists

The official War API is a state-oriented HTTP source, but Foxhole-Data intends to provide durable history and reproducible interpretation.

That requires a hard boundary between:

~~~text
what the source actually returned
                |
                v
what a parser later believes it means
~~~

M2 owns the first side only.

The evidence kernel must remain correct even if:

- the process dies immediately before a source request;
- the process dies immediately after a source request;
- the source returns bytes and the database COMMIT result is lost to the client;
- two workers race for the same job;
- a worker pauses long enough to lose its lease;
- an older worker resumes after a newer worker has taken endpoint ownership;
- the same payload bytes are returned repeatedly;
- a later parser version disagrees with an earlier parser version;
- a source request succeeds but later normalization fails.

The kernel therefore optimizes for auditability and recoverability, not an illusion of exactly-once networking.

## 2. M2 hard scope

M2 MUST implement:

- source/shard/endpoint registry;
- durable collection jobs;
- worker leases with monotonic lease generation;
- durable ingestion attempts;
- endpoint fencing with monotonic fence tokens;
- explicit exchange authorization;
- raw source-exchange evidence;
- immutable raw payload storage;
- SHA-256 identity over exact supplied bytes;
- idempotent payload deduplication;
- raw-capture transactions;
- recovery of expired jobs/attempts;
- detection of unknown exchange outcome;
- stale-fence behavior;
- first real PostgreSQL migration;
- evidence-kernel metrics/traces;
- concurrency and fault/recovery tests.

M2 MUST NOT implement:

- the official War API HTTP adapter;
- official endpoint URLs;
- source DTOs or JSON parsing;
- ETag/cache eligibility behavior;
- source polling cadence;
- normalization;
- quality rules;
- canonical wars/regions/maps/objectives;
- state reconstruction;
- public data endpoints;
- SSE/webhooks/exports;
- production S3/object storage;
- Kafka/Redis/RabbitMQ/NATS;
- automatic external-source retries.

## 3. M2.0 prerequisite governance cleanup

Before the first evidence-schema implementation commit, add a root AGENTS.md.

It must remain short and normative.

Required content:

- authority order starts at .work/README.md;
- read the relevant subsystem specification before changing a subsystem;
- no Core -> Infrastructure/transport dependency;
- no Application -> Infrastructure dependency;
- source adapters do not bypass Evidence/Ingestion;
- no new infrastructure without evidence + ADR;
- build/test commands;
- branch/PR requirement;
- implementation PR updates .work when it changes a documented invariant;
- do not modify adjacent subsystems without necessity.

This is a repository-governance prerequisite, not an Evidence Kernel feature.

## 4. Core design decisions

### 4.1 PostgreSQL remains the operational authority

M2 uses PostgreSQL 18 as the only authoritative durable store.

No additional coordination system is introduced.

Workflow timestamps that define ownership/state transitions use database transaction time. Source-observation timestamps remain explicit evidence supplied by the caller and are never silently replaced by database time.

Leases, fencing, jobs, attempts, raw payloads and fetch evidence all live in the same database, which gives M2 one transactional authority.

### 4.2 Raw payload storage is PostgreSQL-inline in M2

M2 stores exact raw payload bytes in PostgreSQL bytea.

Rationale:

- payload sizes have not yet been measured;
- PostgreSQL TOAST already handles large variable-length values out-of-line internally;
- introducing an external blob store creates a second durability boundary;
- M4 measurement is the correct point to decide whether external CAS is operationally justified.

M2 therefore does NOT implement production filesystem/S3/MinIO CAS.

The schema and Application semantics must still avoid assuming that a payload will always be inline forever.

A later external-CAS implementation must preserve the same PayloadId, SHA-256, byte length and provenance semantics.

### 4.3 Application-generated UUIDv7

All durable entity/operation IDs introduced by M2 are created by the application with Guid.CreateVersion7().

The database stores them as uuid but does not generate them by default.

Reason:

- an operation can have a stable identifier before entering a transaction;
- retry/recovery code can query the exact operation after an unknown commit outcome;
- IDs remain time-ordered;
- ownership of ID generation is consistent across tests and production.

PostgreSQL 18 uuidv7() remains available for SQL-only operational work, but is not the normal entity-ID source.

### 4.4 Read Committed plus explicit atomic SQL

M2 keeps PostgreSQL's normal Read Committed transaction isolation.

Correctness comes from:

- short transactions;
- row locks;
- FOR UPDATE SKIP LOCKED for work claiming;
- monotonic generation/fence counters;
- conditional UPDATE predicates;
- unique constraints;
- INSERT ... ON CONFLICT where idempotency requires it.

Do not switch the whole database or application to Serializable.

### 4.5 No advisory locks for core correctness

PostgreSQL advisory locks are useful, but M2 does not require them.

Job and endpoint ownership is durable data, not connection/session state.

Use explicit rows and counters so ownership remains inspectable after process failure.

Advisory locks MAY later be used as a non-authoritative optimization only if measurement proves value.

### 4.6 xmin is not a business fence token

Npgsql can map PostgreSQL xmin as an optimistic concurrency token.

M2 does not use xmin for leaseGeneration or fenceToken.

Those values have durable domain meaning and must be explicit monotonic bigint columns that can be logged, asserted and reasoned about independently of PostgreSQL transaction IDs.

### 4.7 Database time is authoritative for leases

Lease expiry uses PostgreSQL database time, not worker wall-clock time. Eligibility/expiry checks that run after row-lock waits use `clock_timestamp()` so transaction start time cannot keep an already-expired lease alive.

Worker clocks can differ.

Application TimeProvider remains useful for tests and non-authoritative observation timestamps, but it does not decide lease expiry.

## 5. M2 module placement

M2 extends existing projects; no new production project is required.

Suggested layout:

~~~text
src/
  FoxData.Core/
    Sources/
    Ingestion/
    Evidence/

  FoxData.Application/
    Sources/
    Ingestion/
    Evidence/

  FoxData.Infrastructure/
    Persistence/
      Sources/
      Ingest/
      Evidence/
      Migrations/
    Ingestion/
    Evidence/

tests/
  FoxData.UnitTests/
  FoxData.IntegrationTests/
  FoxData.RecoveryTests/
~~~

Do not create generic Repository<T> abstractions.

The atomic operations are domain-specific and should remain explicit.

## 6. First migration

The first real migration is created in M2.

Suggested name:

~~~text
M2EvidenceKernelInitial
~~~

It creates only these PostgreSQL schemas:

~~~text
sources
ingest
evidence
~~~

Do not create runtime, identity, quality or distribution schemas/tables yet.

No automatic migration is run from Api or Worker startup.

Migration execution remains explicit.

## 7. Source registry

### 7.1 sources.sources

~~~text
id                uuid primary key
key               text not null
display_name      text not null
enabled           boolean not null default true
created_at        timestamptz not null
updated_at        timestamptz not null

unique(key)
~~~

M2 does not seed the official War API in production.

### 7.2 sources.shards

~~~text
id                uuid primary key
source_id         uuid not null
key               text not null
display_name      text not null
environment       text not null
enabled           boolean not null default true
created_at        timestamptz not null
updated_at        timestamptz not null

foreign key source_id -> sources.sources(id)
unique(source_id, key)
~~~

Environment remains text so future source adapters are not forced into a closed database enum.

### 7.3 sources.endpoints

~~~text
id                uuid primary key
shard_id          uuid not null
capability_key    text not null
semantic_key      text not null
enabled           boolean not null default true
created_at        timestamptz not null
updated_at        timestamptz not null

foreign key shard_id -> sources.shards(id)
unique(shard_id, semantic_key)
~~~

M2 endpoint records do NOT contain official URLs. M3 owns mapping semantic endpoints to source request details.

RegisterEndpoint creates its ingest.endpoint_state row in the same transaction so fencing never depends on a later lazy bootstrap.

## 8. Collection job model

Table:

~~~text
ingest.collection_jobs
~~~

Columns:

~~~text
id                    uuid primary key
endpoint_id           uuid not null
idempotency_key       text not null
scheduled_for         timestamptz not null
available_at          timestamptz not null
priority              smallint not null default 0
state                 text not null
lease_owner_id        uuid null
lease_generation      bigint not null default 0
lease_expires_at      timestamptz null
attempt_count         integer not null default 0
created_at            timestamptz not null
updated_at            timestamptz not null
completed_at          timestamptz null
~~~

Constraints:

~~~text
foreign key endpoint_id -> sources.endpoints(id)
unique(endpoint_id, idempotency_key)
lease_generation >= 0
attempt_count >= 0
~~~

Initial job states:

~~~text
pending
leased
processing
completed
failed
cancelled
~~~

State is an internal stable token stored as text rather than PostgreSQL enum. The M2 migration adds a CHECK constraint for the states known to M2; extending the state machine therefore remains an explicit reviewed migration rather than an implicit arbitrary string.

## 9. Atomic job claim

Job claiming is an Infrastructure operation implemented with explicit PostgreSQL SQL.

Conceptually:

~~~sql
WITH candidate AS (
    SELECT id
    FROM ingest.collection_jobs
    WHERE state = 'pending'
      AND available_at <= transaction_timestamp()
    ORDER BY available_at, priority DESC, id
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE ingest.collection_jobs AS job
SET state = 'leased',
    lease_owner_id = @worker_id,
    lease_generation = job.lease_generation + 1,
    lease_expires_at = transaction_timestamp() + @lease_duration,
    updated_at = transaction_timestamp()
FROM candidate
WHERE job.id = candidate.id
RETURNING job.*;
~~~

Properties:

- concurrent workers do not claim the same row;
- waiting workers skip already locked candidates;
- each successful claim advances leaseGeneration;
- database time determines expiry;
- ordering is deterministic.

This query is intentionally PostgreSQL-specific and belongs in Infrastructure.

## 10. Lease semantics

A lease is valid only when all match:

~~~text
jobId
leaseOwnerId
leaseGeneration
state in {leased, processing}
leaseExpiresAt > database now
~~~

Renewal and completion use conditional UPDATE predicates that include owner UUID + generation. Worker instance IDs are application-generated UUIDv7 values, not hostnames or process IDs.

If zero rows update, ownership is lost.

A stale worker must not continue semantic mutation after losing its lease.

M2 does not freeze a War API production lease duration; M3 source timeout design must fit inside or renew the lease.

## 11. Ingestion attempt model

Table:

~~~text
ingest.attempts
~~~

Columns:

~~~text
id                        uuid primary key
job_id                    uuid not null
attempt_number            integer not null
lease_generation          bigint not null
fence_token               bigint null
state                     text not null
outcome_code              text null
started_at                timestamptz not null
exchange_authorized_at    timestamptz null
raw_durable_at            timestamptz null
completed_at              timestamptz null
recovered_at              timestamptz null
superseded_at             timestamptz null
error_class               text null
error_code                text null
created_at                timestamptz not null
updated_at                timestamptz not null

foreign key job_id -> ingest.collection_jobs(id)
unique(job_id, attempt_number)
~~~

Initial attempt states:

~~~text
created
fenced
exchange_authorized
raw_durable
completed
failed
uncertain
superseded
captured_late
~~~

Transition rules are centralized and tested. The M2 migration adds a CHECK constraint for the M2 attempt states. Random call sites do not assign arbitrary state strings.

## 12. Beginning an attempt

BeginAttempt requires a currently valid job lease.

Inside one short transaction:

1. lock/validate current job ownership;
2. increment job.attempt_count;
3. create attempt with that attempt_number;
4. copy current lease_generation;
5. move job to processing if needed.

The caller already knows AttemptId before the transaction.

If COMMIT outcome is unknown, recovery queries by that exact AttemptId instead of blindly creating another attempt.

## 13. Endpoint fencing

Table:

~~~text
ingest.endpoint_state
~~~

One row per endpoint.

~~~text
endpoint_id            uuid primary key
fence_token            bigint not null default 0
active_attempt_id      uuid null
last_current_capture_attempt_id    uuid null
updated_at             timestamptz not null

foreign key endpoint_id -> sources.endpoints(id)
foreign key active_attempt_id -> ingest.attempts(id)
foreign key last_current_capture_attempt_id -> ingest.attempts(id)
~~~

M3 adds ETag/cache eligibility fields; M2 does not create speculative cache columns.

Acquire fence is one short transaction:

1. lock/validate the attempt and its job;
2. verify lease_owner_id, lease_generation and non-expired lease;
3. verify the attempt belongs to the endpoint being fenced;
4. increment the endpoint fence and attach the attempt.

The endpoint update is conceptually:

~~~sql
UPDATE ingest.endpoint_state
SET fence_token = fence_token + 1,
    active_attempt_id = @attempt_id,
    updated_at = transaction_timestamp()
WHERE endpoint_id = @endpoint_id
RETURNING fence_token;
~~~

Fence tokens monotonically increase per endpoint.

A newer fence does not delete old evidence. It only prevents old work from advancing the endpoint current-capture marker. Current raw capture is not canonical acceptance.

~~~text
stale response may still be evidence
stale response must not become current raw capture
~~~

## 14. Exchange authorization

M2 writes a durable phase marker before any external source call.

AuthorizeExchange requires:

- valid lease;
- matching lease generation;
- expected current fence;
- exchange_authorized_at is null;
- attempt state fenced.

The transaction writes state=exchange_authorized and exchange_authorized_at.

After this commit, that AttemptId is permanently considered to have potentially performed its one external exchange.

The same AttemptId MUST NEVER be used to perform a second external request after authorization.

A retry is a new attempt.

## 15. Why authorization comes before network I/O

There is no atomic transaction spanning PostgreSQL and an external HTTP server.

Durable authorization gives honest states:

~~~text
not authorized
  -> definitely no source call permitted

authorized + no durable fetch
  -> source-call outcome may be uncertain

authorized + durable fetch
  -> response evidence exists
~~~

M2 does not pretend exactly-once HTTP is possible.

## 16. Fetch evidence

Table:

~~~text
evidence.fetches
~~~

Columns:

~~~text
id                    uuid primary key
attempt_id            uuid not null
endpoint_id           uuid not null
request_started_at    timestamptz not null
response_started_at   timestamptz null
retrieved_at          timestamptz not null
transport_kind        text not null
status_code           integer null
media_type            text null
content_encoding      text null
declared_length       bigint null
source_etag           text null
cache_control         text null
expires_at            timestamptz null
payload_id            uuid null
prior_fetch_id        uuid null
duration_ms           bigint not null
created_at            timestamptz not null

foreign key attempt_id -> ingest.attempts(id)
foreign key endpoint_id -> sources.endpoints(id)
foreign key payload_id -> evidence.payloads(id)
foreign key prior_fetch_id -> evidence.fetches(id)
unique(attempt_id)
duration_ms >= 0
declared_length is null or declared_length >= 0
~~~

The one-fetch-per-attempt unique constraint is part of the audited one-exchange model.

M2 test fixtures can use transport_kind=fixture. M3 introduces real HTTP semantics.

## 17. Raw payload model

Table:

~~~text
evidence.payloads
~~~

Columns:

~~~text
id               uuid primary key
sha256           bytea not null
byte_length      bigint not null
body             bytea not null
created_at       timestamptz not null

unique(sha256)
check octet_length(sha256) = 32
check byte_length >= 0
check octet_length(body) = byte_length
~~~

Hash is SHA-256 over the exact byte sequence supplied to the Evidence Kernel before semantic parsing.

M2 does not apply application-level compression. PostgreSQL/TOAST physical storage remains transparent.

### Collision behavior

A SHA-256 conflict is not blindly treated as deduplication.

On conflict:

1. compare byte length;
2. compare exact bytes when necessary;
3. differing bytes under the same hash fail closed and emit a critical integrity signal;
4. only identical bytes reuse PayloadId.

## 18. Raw capture transaction

CaptureSourceResponse performs one database transaction.

Inputs include AttemptId, EndpointId, expected leaseGeneration, expected fenceToken, response metadata and exact payload bytes or explicit no-body result.

Steps:

1. verify attempt exists and was exchange-authorized;
2. calculate SHA-256;
3. insert/reuse payload;
4. insert immutable fetch row;
5. set raw_durable_at;
6. determine whether fence remains current;
7. classify current vs captured_late/superseded;
8. set `endpoint_state.last_current_capture_attempt_id` only when the expected lease/fence are still current;
9. complete/release the source collection job at the raw-durability boundary;
10. COMMIT.

`last_current_capture_attempt_id` records transport/currentness only. Parsing, quality, identity and canonical acceptance occur later over immutable evidence and do not reuse this source collection lease.

No canonical Foxhole interpretation occurs.

### Unknown COMMIT outcome

If connection is lost during COMMIT:

- never repeat the source exchange;
- query evidence.fetches by AttemptId;
- if fetch exists, raw capture committed;
- if no fetch exists, recovery classifies observed durable state;
- stable AttemptId is the recovery key.

## 19. No-body / 304-ready model

M2 supports no-body evidence without implementing HTTP caching.

For a no-body validation:

~~~text
payload_id = null
prior_fetch_id = previous representation fetch
~~~

M3 decides how ETag/304 produces this input.

No duplicate payload row is created.

## 20. Payload deduplication is not observation deduplication

If identical bytes are fetched 100 times:

~~~text
1 payload
100 fetch records
~~~

Payload answers what exact bytes existed.

Fetch answers when/from which endpoint/attempt they were observed or validated.

Never collapse those concepts.

## 21. Evidence immutability

M2 evidence persistence exposes insert/read/provenance lookup.

It does not expose update/replace methods for payloads or fetches.

Mutable workflow state belongs in ingest.

Hard database DELETE-prevention triggers are deferred until retention/lifecycle policy exists.

## 22. Recovery classifier

Recovery scans expired leased/processing jobs in bounded locked batches.

### Case A — no attempt

Clear expired lease; job -> pending.

### Case B — attempt exists but exchange not authorized

Attempt -> failed with abandoned_before_exchange; job -> pending.

Safe because the attempt never received permission to touch the source.

### Case C — exchange authorized, no fetch

Attempt -> uncertain with uncertain_exchange.

The old attempt is never reused. A later retry uses a NEW attempt.

### Case D — fetch exists

Repair job/attempt state from observed durable evidence. Do not perform source request.

### Case E — stale worker later captures after recovery

Raw evidence MAY still be inserted for that old attempt if no fetch exists.

Classify captured_late/superseded.

It MUST NOT overwrite current endpoint authority when its fence is stale.

## 23. Recovery idempotency

Recovery may itself crash.

Every recovery mutation uses conditional current-state/generation predicates and uniqueness constraints.

Running recovery twice converges to the same durable state.

## 24. No hidden transaction retry

M2 uses explicit transactions for state-machine boundaries.

Do not wrap them in an implicit retrying execution strategy.

A database error during COMMIT can have unknown outcome.

Recovery observes stable IDs rather than assuming rollback and rerunning the whole operation.

## 25. EF Core versus explicit SQL

Use EF Core for:

- migrations;
- registry CRUD;
- basic evidence lookup;
- ordinary inserts/queries.

Use explicit Npgsql SQL where the database primitive is the architecture:

- FOR UPDATE SKIP LOCKED claim;
- conditional lease renewal;
- fence increment + RETURNING;
- bounded recovery claiming;
- collision-safe payload insert where needed.

Do not force critical concurrency semantics through complex LINQ just to remain provider-neutral.

## 26. Index plan

Initial indexes beyond primary/unique constraints:

### collection_jobs

~~~text
(available_at, priority DESC, id)
WHERE state = 'pending'

(lease_expires_at, id)
WHERE state IN ('leased', 'processing')

(endpoint_id, scheduled_for)
~~~

### attempts

~~~text
(job_id, attempt_number)
(state, started_at)
~~~

### fetches

~~~text
(endpoint_id, retrieved_at DESC, id)
(payload_id)
(prior_fetch_id)
~~~

### payloads

Unique B-tree index on sha256.

No speculative indexes before M3/M4 measurements.

## 27. Delete behavior

Use restrictive foreign keys by default.

Avoid cascade delete from source/shard/endpoint into jobs/evidence.

Registry records are disabled, not physically deleted, when provenance exists.

## 28. Worker identity

Each Worker process has a runtime instance UUIDv7.

lease_owner_id stores that UUID directly.

Hostname/process ID may be logged but is not the uniqueness authority.

## 29. M2 configuration

Suggested options:

~~~text
IngestionKernelOptions
  LeaseDuration
  RecoveryBatchSize
~~~

M2 does not add WarApiOptions, polling intervals, HTTP timeout or ETag policy.

Test config may use short leases. Production values stay conservative until M3.

## 30. Application use cases

Prefer semantic operations over CRUD repositories:

~~~text
RegisterSource
RegisterShard
RegisterEndpoint

EnqueueCollectionJob
ClaimCollectionJob
RenewCollectionJobLease

BeginIngestionAttempt
AcquireEndpointFence
AuthorizeSourceExchange
DeferBeforeExchange
DeferUncertainExchange

CaptureSourceResponse
CompleteIngestionAttempt

RecoverExpiredIngestionWork

GetAttemptEvidence
GetFetchEvidence
GetPayload
~~~

These are internal Application operations, not public HTTP endpoints.

## 31. Typed operation outcomes

Expected concurrency states are typed results, not exceptions.

Examples:

~~~text
Claim: NoneAvailable | Claimed
Lease: Renewed | Lost
Fence: Acquired | LeaseLost
Authorization: Authorized | AlreadyAuthorized | StaleFence | LeaseLost
Deferral: DeferredNow | AlreadyDeferred | LeaseLost | InvalidState
Capture: CapturedCurrent | CapturedLate | AlreadyCaptured | InvalidAttempt
~~~

Integrity violations remain exceptions/errors.

## 32. M2 observability

Add Evidence Kernel ActivitySource/Meter.

Suggested metrics:

~~~text
foxdata.ingest.jobs.enqueued
foxdata.ingest.jobs.claimed
foxdata.ingest.lease.lost
foxdata.ingest.attempts.started
foxdata.ingest.attempts.uncertain
foxdata.ingest.fence.conflicts
foxdata.evidence.fetches.captured
foxdata.evidence.payload.bytes
foxdata.evidence.payload.dedupe_hits
foxdata.evidence.capture.duration
foxdata.ingest.recovery.items
~~~

Metric labels may include bounded source/environment/outcome/operation.

Do NOT use JobId, AttemptId, EndpointId or payload hash as metric labels.

Those IDs belong in traces/logs.

Representative spans:

~~~text
ingest.enqueue
ingest.claim
ingest.begin_attempt
ingest.acquire_fence
ingest.authorize_exchange
evidence.capture
ingest.recover
~~~

## 33. Logging

Structured transition logs may include:

~~~text
JobId
AttemptId
EndpointId
LeaseGeneration
FenceToken
Outcome
PayloadHash
PayloadLength
~~~

Never log payload body.

Expected claim contention is low-level telemetry, not warning spam.

Uncertain outcome is warning-level; integrity mismatch is error/critical.

## 34. Unit tests

Test:

- UUIDv7 wrappers;
- payload SHA-256;
- state-transition validation;
- invalid transitions;
- lease/fence value semantics;
- recovery classifier;
- hash rendering/parsing;
- option validation.

Do not mock EF Core for PostgreSQL semantics.

## 35. PostgreSQL integration tests

Using PostgreSQL 18.6 Testcontainers, verify at least:

1. migration applies to empty database;
2. only M2 schemas/tables are created;
3. registry uniqueness;
4. job idempotency uniqueness;
5. concurrent duplicate enqueue converges;
6. two concurrent claims cannot receive same job;
7. SKIP LOCKED allows another worker to claim another row;
8. lease generation increases after reclaim;
9. stale owner cannot renew;
10. stale owner cannot complete;
11. fence token monotonically increases;
12. stale fence cannot advance endpoint state;
13. attempt number increments per job;
14. unique fetch per attempt;
15. identical bytes reuse PayloadId;
16. different bytes get different PayloadId;
17. byte-length constraint rejects mismatch;
18. SHA length constraint rejects malformed hash;
19. no-body fetch can reference prior fetch;
20. raw capture succeeds with current fence;
21. late capture preserves evidence but not authority;
22. unknown-commit reconciliation uses stable AttemptId;
23. recovery is idempotent.

## 36. Concurrency stress tests

Use deterministic barriers/channels rather than probabilistic sleeps.

Example:

~~~text
100 pending jobs
8 claimant tasks
no two live claims share job + lease generation
all jobs eventually accounted for
~~~

Fence stress:

~~~text
same endpoint
N contending attempts
tokens strictly increase
only current token advances endpoint authority
~~~

## 37. Recovery/fault tests

FoxData.RecoveryTests becomes substantive.

Required fault points:

- after job-claim COMMIT;
- after BeginAttempt COMMIT;
- after fence COMMIT;
- after exchange-authorization COMMIT before source call;
- after simulated response before raw capture;
- during raw-capture COMMIT with unknown client result;
- after fetch exists before completion;
- stale late capture after a newer fence.

The exchange-authorized-before-call crash is intentionally classified uncertain. False uncertainty is acceptable; false certainty is not.

## 38. Source simulation in M2

Mandatory tests use a deterministic in-process fixture exchange executor.

It can produce:

- body bytes;
- no body;
- exception before exchange;
- exception during exchange;
- delayed response;
- duplicate bytes;
- distinct bytes.

It is test-only and never calls the public Internet.

M3 replaces it with the actual source adapter.

## 39. Migration verification

CI must test:

~~~text
empty PostgreSQL -> migrate -> M2 integration suite
~~~

No Api/Worker process auto-migrates.

Down migration/destructive behavior is reviewed explicitly.

## 40. Data retention

M2 implements no evidence deletion/compaction.

M4 measurements and later lifecycle work decide retention.

## 41. Security

Evidence kernel treats bytes as opaque potentially hostile data:

- persist as bytea;
- parameterize SQL;
- do not log body;
- caller/config provides an upper byte bound;
- no filesystem path from source data;
- no arbitrary URL handling.

## 42. Performance philosophy

Correctness first, but:

- no network work inside DB transactions;
- indexed bounded queue claim;
- bounded recovery batches;
- payload bytes not loaded by ordinary job queries;
- SHA unique index handles dedupe;
- no unbounded scans.

## 43. M2 implementation slices

### M2.0 — Governance prerequisite

Deliver root AGENTS.md and confirm Dependency Review now executes with enabled Dependency Graph.

### M2.1 — Domain primitives and first migration

Typed IDs, persistence models/configurations and M2EvidenceKernelInitial migration.

### M2.2 — Source registry

Register/read Source, Shard and Endpoint with idempotent semantics.

### M2.3 — Durable queue and leases

Enqueue, SKIP LOCKED claim, renew, safe lease release and lease generation.

Successful job completion is deliberately coupled to the M2-C raw-capture boundary: a job MUST NOT become completed before durable evidence exists.

### M2.4 — Attempts and endpoint fencing

BeginAttempt, endpoint_state, monotonic fence, transition validator and one-way exchange authorization.

AttemptId is caller-generated before BeginAttempt. Concurrent/retried BeginAttempt calls with the same AttemptId converge to the same durable attempt. Only an `AuthorizedNow` result grants permission to perform the attempt's single external exchange; `AlreadyAuthorized` is observation-only and MUST NOT trigger another exchange.

### M2.5 — Inline evidence payloads

SHA-256 PayloadHash, bytea storage, constraints, collision-safe dedupe and immutable fetch insertion.

### M2.6 — Raw capture transaction

CaptureSourceResponse, no-body/prior-fetch support, stable-ID reconciliation and late-capture classification.

### M2.7 — Recovery

Expired-work scan, uncertain outcome handling, state repair and bounded idempotent recovery.

### M2.8 — Observability/hardening

M2 ActivitySource/Meter, logs, configuration bounds and index review.

### M2.9 — Final gate

Complete integration/concurrency/recovery suite, migration-from-empty CI and M2 completion record.

## 44. Recommended PR sequence

~~~text
PR M2-A  governance + schema + registry
PR M2-B  queue + lease + attempt + fence
PR M2-C  payload + fetch + raw capture
PR M2-D  recovery + fault tests + observability + completion
~~~

Each PR leaves main buildable.

## 45. Definition of Done

M2 is DONE only when:

### Governance

- root AGENTS.md exists;
- this specification is authoritative;
- M2 ADRs are accepted.

### Persistence

- first real migration exists;
- only sources/ingest/evidence M2 schemas are introduced;
- PostgreSQL 18.6 migration from empty succeeds;
- no canonical runtime tables exist.

### Registry

- Source/Shard/Endpoint are durable and uniquely constrained;
- production DB contains no fake test fixtures;
- official URLs remain M3.

### Queue

- enqueue is idempotent;
- concurrent claim uses SKIP LOCKED;
- leaseGeneration monotonically advances;
- DB time controls expiry;
- stale ownership cannot mutate current job state.

### Attempts/fencing

- IDs are application UUIDv7;
- attempt numbers unique per job;
- fence tokens monotonically increase;
- stale fences cannot advance the current raw-capture marker;
- exchange authorization is durable and one-way.

### Evidence

- raw body is bytea;
- SHA-256 is exact kernel-input bytes;
- hash length/body length constraints enforced;
- identical bytes reuse payload;
- separate fetches remain separate observations;
- one fetch per attempt;
- no-body/prior-fetch supported.

### Recovery

- pre-exchange expiry can retry safely;
- authorized-without-fetch becomes uncertain;
- same authorized attempt is never replayed;
- unknown COMMIT is resolved through durable observation;
- late stale evidence cannot become the current raw capture;
- recovery is idempotent.

### Testing/observability

- migration, concurrency, fence, payload and fault tests green;
- no public War API dependency;
- no payload bodies in logs;
- no high-cardinality metric IDs.

### Scope discipline

M2 contains no official War API client/parser, ETag scheduler, canonical Foxhole model, S3/MinIO or message broker.

## 46. Handoff to M3

M3 should be able to add:

~~~text
semantic endpoint
      |
BuildRequest
      |
AuthorizeExchange
      |
exactly one HttpClient.SendAsync
      |
response metadata + opaque bytes
      |
CaptureSourceResponse
~~~

M3 may add source registration, request builders, response limits, conditional headers, ETag/cache semantics, source DTOs, parsing and structural fingerprints.

M3 MUST NOT need to redesign job claiming, leases, attempt identity, endpoint fencing, exchange authorization, payload hashing, fetch identity or raw-capture recovery.

If it does, M2 is incomplete.
