# M3 Official War API Adapter completion record

Status: completed.
Completion date: 2026-09-20.
Implementation PR: #14.
Predecessor: M2 Evidence Kernel.
Successor: M4 source measurement.

This record closes the milestone defined by [M3_OFFICIAL_WAR_API_ADAPTER.md](M3_OFFICIAL_WAR_API_ADAPTER.md).

## Delivery

M3 implements the official Foxhole War API as an evidence-first source adapter without introducing canonical Foxhole state.

Delivered endpoint families:

- GET /worldconquest/war;
- GET /worldconquest/maps;
- GET /worldconquest/warReport/{mapName};
- GET /worldconquest/maps/{mapName}/static;
- GET /worldconquest/maps/{mapName}/dynamic/public.

Delivered runtime slices:

- M3-A — source contracts, endpoint catalog, DTOs, fixtures and source semantics;
- M3-B — endpoint poll state, current-representation read model and versioned source parse runs;
- M3-C — tolerant parser, open upstream values and json-shape@1 structural fingerprinting;
- M3-D — fixed-root HTTP transport, conditional GET, cache eligibility, Retry-After/backoff and bounded wire/decoded content;
- M3-E — registry bootstrap, M2 recovery, source-filtered claiming, executor, planner/reconciler, map discovery, deterministic spreading and source telemetry;
- M3-F — disabled-by-default runtime configuration, controlled activation procedure and a non-blocking live canary workflow.

## Source and provenance boundary

Production request construction is restricted to adapter-owned roots:

~~~text
live-1 -> https://war-service-live.foxholeservices.com/api
live-2 -> https://war-service-live-2.foxholeservices.com/api
live-3 -> https://war-service-live-3.foxholeservices.com/api
dev    -> https://war-service-dev.foxholeservices.com/api
~~~

Live shards remain distinct provenance domains. Dev is separately identified and requires explicit opt-in.

Production configuration does not accept an arbitrary source base URL.

Map names remain exact opaque source identifiers. Path separators, control characters and URI dot-segments are rejected before derived endpoint construction; source bytes remain preserved independently.

## Durable collection model delivered

M3 reuses the M2 correctness path:

~~~text
CollectionJob
  -> Claim
  -> BeginAttempt
  -> Endpoint Fence
  -> deterministic request construction
  -> AuthorizeExchange
  -> at most one FoxData application-issued SendAsync
  -> immutable Fetch / optional Payload
  -> versioned parse run
  -> poll-state reconciliation
  -> deterministic successor
~~~

No PostgreSQL transaction spans network I/O.

No application retry, hedging or redirect handler was added.

A later FoxData-issued source request always receives a new AttemptId.

The executor claims only jobs belonging to source key official-war-api, so future adapters cannot be accidentally consumed by the War API worker.

## HTTP and representation semantics verified

The implementation establishes:

- AllowAutoRedirect=false;
- AutomaticDecompression=None;
- UseCookies=false;
- bounded connect, header, exchange, wire-body and decoded-body limits;
- HttpCompletionOption.ResponseHeadersRead;
- strong and weak ETag preservation;
- syntactic validation before If-None-Match replay;
- explicit Date/Age/Cache-Control/Expires/Retry-After evidence;
- source freshness takes precedence over a faster local cadence;
- no-store responses are evidence but are not reusable validator representations;
- 304 creates a bodyless Fetch linked directly to the body-bearing representation;
- repeated validation does not duplicate Payload;
- orphan 304 clears validator reuse and schedules an unconditional successor;
- status/headers remain durable evidence when body capture is oversized, cancelled, times out or fails mid-stream;
- incomplete body evidence never becomes a reusable representation;
- exact captured content bytes are hashed before semantic decoding.

The .NET transport-runtime connection-recovery boundary is documented separately by ADR-0015; the durable invariant concerns FoxData application-issued sends.

## Parsing and drift evidence verified

M3 parsing is deliberately tolerant and non-canonical.

Verified behavior includes:

- null pre-conquest war timestamps;
- additive unknown properties through JsonExtensionData;
- unknown team values;
- open icon/flag integer values;
- upstream iconType 97 and viewDirection;
- exact MarbanHollow / DeadLandsHex identity;
- dedicated war-report parsing;
- static and dynamic map source shapes;
- malformed JSON distinguished from syntactically valid but incompatible typed shape;
- json-shape@1 ignores ordinary value and ordering changes while detecting additive/type-shape drift;
- parser/fingerprint results are persisted as versioned evidence.source_parse_runs;
- parse-run replay is idempotent even when a restarted execution has different execution timestamps;
- raw Fetch durability always precedes source parsing.

A parser failure does not erase or rewrite raw evidence.

## Polling and crash recovery verified

M3 adds ingest.endpoint_poll_state as a rebuildable operational projection. It does not duplicate M2 lease/fence authority.

Verified recovery properties include:

- Fetch COMMIT followed by process failure does not permanently lose successor scheduling;
- deterministic after-fetch:{FetchId} jobs make replay idempotent;
- existing successor + missing poll-state is repaired without another upstream request;
- transport failure after authorization becomes uncertain and does not authorize a second send for the same AttemptId;
- M2 expired-work recovery is active in the production Worker topology;
- parse work can replay from durable bytes without HTTP;
- regional endpoint discovery is idempotent;
- repeated /maps refreshes do not create duplicate initial discovery jobs;
- disappeared maps remain registered for provenance but do not receive new normal successors after their next regional observation is reconciled inactive;
- HomeRegionC/HomeRegionW do not receive assumed static/dynamic map endpoints;
- deterministic phase spreading prevents synchronized regional fan-out.

## Observability and operations

M3 exports FoxData.WarApi tracing and metrics with bounded labels:

~~~text
source
environment
shard
capability
outcome
~~~

Map names, FetchIds, AttemptIds, ETags, payload hashes and war IDs are not broad metric dimensions.

[ M3_ACTIVATION.md ](M3_ACTIVATION.md) defines controlled Live-1 activation, rollback and expansion rules.

WarApi:Enabled remains false by default after M3 completion. Completing the adapter does not silently enable external traffic in normal development, tests or Compose smoke.

The separate war-api-canary workflow is scheduled/manual and non-blocking. Mandatory CI remains independent from the public Internet.

## Persistence delivered

M3 adds forward-only EF Core migrations for:

- ingest.endpoint_poll_state;
- evidence.source_parse_runs;
- HTTP cache/retry evidence metadata on evidence.fetches;
- body_error_code on evidence.fetches.

Schema readiness continues to fail closed when required migrations are pending.

## Final verification gate

Final implementation head before the completion-record-only commit:

~~~text
commit:                         e028c9dde2ad0cbbb4fba38991012b763ed3dad6
CI run:                         35522463372
Release build:                  success
Compiler warnings:              0
Compiler errors:                0
Unit tests:                     24 passed / 0 failed / 0 skipped
Integration tests:              41 passed / 0 failed / 0 skipped
Recovery tests:                 7 passed / 0 failed / 0 skipped
Source tests:                   74 passed / 0 failed / 0 skipped
Contract tests:                 2 passed / 0 failed / 0 skipped
Total tests:                    148 passed / 0 failed / 0 skipped
Docker Compose migration job:   success
Schema-aware API readiness:     success
Docker Compose smoke:           success
Contracts workflow:             success
Dependency Review:              success
~~~

Mandatory tests use PostgreSQL/Testcontainers and deterministic fake source transports; they do not call the public War API.

The completion-record commit itself must pass the same repository CI gates before PR #14 is merged.

## Deliberate M3 exclusions

M3 does not implement:

- canonical war/shard/region/report state;
- canonical static/dynamic map observations;
- quality acceptance/quarantine;
- objective identity;
- historical intervals or replay;
- public historical/query APIs;
- War API compatibility facade;
- aggressive 3-second regional polling;
- Redis, Kafka, RabbitMQ, NATS or a second scheduler;
- cross-shard fallback;
- automatic live-source enablement.

Those boundaries remain owned by later milestones.

## M4 handoff

M4 is now the next implementation milestone.

M4 must use the M3 controlled activation path to run a bounded 48–72 hour source measurement and publish collection-profile@1 from observed evidence.

The measurement should determine at least:

- effective Cache-Control/Expires/Date/Age behavior;
- ETag prevalence and 200/304 ratio;
- response body and encoded/decoded size distributions;
- endpoint change frequency;
- latency and error distributions;
- Retry-After behavior;
- static-data revalidation behavior;
- safe per-capability cadence and concurrency;
- whether PostgreSQL-inline evidence storage remains appropriate at measured volume.

M4 measurement must not weaken M2/M3 attempt, evidence, cache or provenance invariants.
