# M3 — Official War API Adapter

Status: planned.
Planning date: 2026-09-20.
Predecessor: [M2 Evidence Kernel](M2_COMPLETION.md).
Successor: M4 source measurement.

This document is normative for M3 implementation. It refines the milestone summary in [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) without moving canonical Foxhole semantics from M5/M6 into the source adapter.

## 1. Purpose

M3 activates real collection from the documented official Foxhole War API while preserving the M2 correctness model.

The milestone delivers a cache-aware, evidence-first, crash-recoverable source adapter for every documented World Conquest endpoint:

- GET /worldconquest/war;
- GET /worldconquest/maps;
- GET /worldconquest/warReport/{mapName};
- GET /worldconquest/maps/{mapName}/static;
- GET /worldconquest/maps/{mapName}/dynamic/public.

M3 ends after durable source collection, tolerant source parsing, structural fingerprinting and durable poll planning.

It MUST NOT implement canonical war/region/objective state, quality acceptance, objective identity, historical state intervals, public query APIs or the War API compatibility facade.

## 2. Authority and evidence

Implementation MUST read, in authority order:

1. [ARCHITECTURE.md](ARCHITECTURE.md);
2. accepted ADRs, especially ADR-0011, ADR-0013, ADR-0015 and ADR-0016;
3. [WAR_API_SEMANTICS.md](WAR_API_SEMANTICS.md);
4. [SOURCE_ADAPTERS.md](SOURCE_ADAPTERS.md);
5. [INGESTION_AND_RECOVERY.md](INGESTION_AND_RECOVERY.md);
6. [EVIDENCE_AND_PROVENANCE.md](EVIDENCE_AND_PROVENANCE.md);
7. [CACHING_RATE_LIMITS.md](CACHING_RATE_LIMITS.md);
8. [SECURITY.md](SECURITY.md);
9. [OBSERVABILITY.md](OBSERVABILITY.md);
10. [TESTING.md](TESTING.md).

Non-normative September 2026 research is recorded in [research/M3_WAR_API_HTTP_2026-09.md](research/M3_WAR_API_HTTP_2026-09.md).

## 3. M2 handoff invariants

M3 MUST reuse the M2 durable sequence:

~~~text
CollectionJob
    -> claim lease
    -> BeginAttempt(stable AttemptId)
    -> acquire endpoint fence
    -> build deterministic request
    -> AuthorizeExchange
    -> exactly one application-issued HTTP exchange
    -> immutable Fetch / optional Payload
    -> source parsing/fingerprint
    -> durable successor planning
~~~

M3 MUST NOT:

- create a second lease or fence mechanism;
- write evidence tables from the War API adapter;
- hold a PostgreSQL transaction over network I/O;
- authorize a second application HTTP send for the same AttemptId;
- reinterpret last_current_capture_attempt_id as parse or canonical acceptance;
- make public API demand trigger synchronous upstream requests.

A new upstream request after any completed, failed or uncertain application exchange requires a new AttemptId.

## 4. Source roots and provenance domains

The only production roots are fixed adapter-owned values:

~~~text
live-1 -> https://war-service-live.foxholeservices.com/api
live-2 -> https://war-service-live-2.foxholeservices.com/api
live-3 -> https://war-service-live-3.foxholeservices.com/api
dev    -> https://war-service-dev.foxholeservices.com/api
~~~

Registry identity:

~~~text
source.key = official-war-api

shard.key = live-1 | live-2 | live-3 | dev
environment = live | dev
~~~

Live shards are separate provenance domains. They are never failover replicas or load-balancing targets for one another.

Dev is disabled by default and must require explicit opt-in. Dev evidence must remain distinguishable from live evidence at every later stage.

Arbitrary runtime source URLs are forbidden. Tests may replace the HTTP transport through dependency injection; production configuration does not expose an unrestricted base URL.

## 5. Source capabilities and semantic endpoint keys

M3 defines these capabilities:

| Capability | Semantic key | Source path |
| --- | --- | --- |
| RuntimeWarState | war | /worldconquest/war |
| ActiveMapList | maps | /worldconquest/maps |
| RegionWarReport | war-report/{mapName} | /worldconquest/warReport/{mapName} |
| StaticMapState | map-static/{mapName} | /worldconquest/maps/{mapName}/static |
| DynamicMapState | map-dynamic/{mapName} | /worldconquest/maps/{mapName}/dynamic/public |

The semantic key is durable identity. A hostname or source path is not durable identity.

### 5.1 mapName is opaque source identity

The adapter MUST preserve the exact source mapName string.

It MUST NOT:

- append or remove Hex;
- change case;
- derive API identity from asset filenames;
- silently alias source names;
- treat spelling inconsistencies as equivalent.

This is required because the official ecosystem currently contains names such as MarbanHollow and DeadLandsHex, and upstream issue #134 documents API/asset naming inconsistencies.

For request construction, a map name is treated as one bounded path segment. Values that contain path separators, control characters or exceed the configured identifier bound are retained in raw evidence but MUST NOT be converted into a derived HTTP endpoint automatically.

## 6. Registry bootstrap and map discovery

Worker startup/bootstrap ensures, idempotently:

1. source official-war-api;
2. configured shard records;
3. war endpoint for each enabled shard;
4. maps endpoint for each enabled shard;
5. initial collection jobs for those root endpoints.

A successful current maps representation drives per-map endpoint discovery.

For every safe map name:

- register war-report/{mapName};
- register map-static/{mapName} when the source contract supports map data for that name;
- register map-dynamic/{mapName} when the source contract supports map data for that name;
- ensure an initial scheduled job with deterministic idempotency.

Officially documented Home Region quirks are source capability rules, not string-normalization rules. If HomeRegionC or HomeRegionW appears, M3 may collect documented/observed war reports but MUST NOT assume static/dynamic map data exists.

Endpoints that disappear from the newest current map list remain registered for provenance/history. M3 stops planning new active cadence jobs for them; it does not delete registry rows.

A job already durably queued before a map disappears may complete one final source attempt. M3 does not add a second cancellation state machine solely to suppress that bounded race.

## 7. Separate durable poll state

M2 ingest.endpoint_state owns only fence/current-raw-capture authority.

M3 introduces a separate scheduling/validator projection:

~~~text
ingest.endpoint_poll_state
~~~

Conceptual fields:

~~~text
endpoint_id                    PK/FK
last_processed_fetch_id        nullable
latest_validation_fetch_id     nullable
representation_fetch_id        nullable
validator_etag                 nullable
source_cache_eligible_at       nullable
next_target_at                 nullable
retry_eligible_at              nullable
last_http_response_at          nullable
last_success_at                nullable
consecutive_failures           non-negative
policy_version                 required
updated_at
~~~

Meanings:

- last_processed_fetch_id: newest current Fetch incorporated into poll state;
- latest_validation_fetch_id: newest current successful 200/304 validation observation;
- representation_fetch_id: body-bearing Fetch whose bytes are the currently reusable representation;
- validator_etag: validator associated with that reusable representation after 200/304 metadata reconciliation;
- source_cache_eligible_at: earliest source-cache revalidation time;
- next_target_at: local desired cadence target;
- retry_eligible_at: source-specific HTTP failure/backoff lower bound;
- consecutive_failures: HTTP-response failure streak for successor planning.

This table is not a second source-of-truth for M2 ownership and MUST NOT contain a duplicate fence token or lease generation.

It is a rebuildable operational projection over durable source evidence plus configured policy.

## 8. Representation lineage and 304 semantics

M3 distinguishes:

~~~text
latest current Fetch
latest validation Fetch
body-bearing reusable representation Fetch
~~~

These are not always the same row.

Example:

~~~text
Fetch A: 200 + body + ETag "x"
Fetch B: 304 -> prior_fetch_id = A
Fetch C: 304 -> prior_fetch_id = A
~~~

A 304 MUST reference a body-bearing Fetch accepted as the current reusable source representation. M2 already enforces that prior_fetch_id references a Fetch with a payload.

M3 therefore MUST NOT simply use last_current_capture_attempt_id as the representation pointer.

### 8.1 successful 200

For an eligible 200 response with a captured body:

- capture exact content bytes;
- promote its Fetch as representation_fetch_id;
- update validator metadata from the response;
- run parser/fingerprint after raw durability;
- calculate successor eligibility.

Parser failure does not erase or mutate the representation. A later parser version may reprocess the same bytes.

### 8.2 successful 304

For 304:

- capture a no-body Fetch;
- set prior_fetch_id = representation_fetch_id;
- keep the existing body-bearing representation;
- reconcile cache metadata/ETag from the 304 according to HTTP semantics;
- do not create a duplicate Payload;
- do not rerun parsing solely because a validation request occurred.

### 8.3 orphan 304

A 304 without a known body-bearing representation is inconsistent local state.

M3 must:

- preserve the 304 Fetch when capture is valid;
- record an orphan_304 diagnostic;
- clear or avoid validator reuse;
- schedule a new unconditional request with a new AttemptId;
- never fabricate a body or prior representation.

## 9. HTTP application-exchange boundary

Per ADR-0015, the durable M2 invariant is:

> one AttemptId permits at most one application-issued HttpClient.SendAsync or HttpMessageInvoker.SendAsync operation.

M3 MUST NOT attach:

- AddStandardResilienceHandler;
- a retry handler;
- hedging;
- request replay middleware;
- automatic redirect following;
- manual retry loops around the send.

The .NET 10 SocketsHttpHandler may internally recover/retry some connection-level failures. Those implementation-internal actions are not separately authorized application sends and are not modeled as new durable attempts.

If FoxData itself decides to perform another request, that request always uses a new AttemptId.

## 10. HTTP handler baseline

The production source client uses a long-lived SocketsHttpHandler / HttpClient managed through DI.

Required baseline:

~~~text
AllowAutoRedirect = false
AutomaticDecompression = None
UseCookies = false
credentials = none
ConnectTimeout = bounded
MaxConnectionsPerServer = bounded
MaxResponseHeadersLength = bounded
HttpClient.Timeout = InfiniteTimeSpan
HttpCompletionOption = ResponseHeadersRead
~~~

The source adapter owns a full exchange deadline using TimeProvider-aware cancellation so the deadline also covers streaming the response body.

No default Accept-Encoding is added by M3. This keeps ordinary source responses directly hashable as returned content bytes and avoids unnecessary decompression complexity.

If the source nevertheless returns a supported Content-Encoding, raw encoded content bytes are captured first. Any decoding for parsing happens afterward with separate decoded-size and expansion-ratio limits.

## 11. What exact raw bytes means over HttpClient

For HTTP evidence, payload identity is SHA-256 over the exact HTTP content bytes exposed by the transport after HTTP transfer framing is removed and before any Content-Encoding decompression.

It does not include:

- TCP/TLS records;
- HTTP/1 chunk framing;
- HTTP/2/3 frame bytes;
- headers.

M3 MUST disable automatic content decompression so gzip/br content is not silently transformed before hashing.

## 12. Request construction

Requests are built only from:

- a fixed configured shard enum/key;
- a known capability;
- a validated opaque map-name segment;
- the current validator state.

Rules:

- method is GET;
- scheme is HTTPS;
- authority must equal the adapter-owned shard root;
- paths are produced from fixed templates;
- map names are escaped as one path segment;
- no user-supplied absolute URI is accepted;
- no redirect is followed;
- no cookies or authentication state is used;
- If-None-Match is emitted as one syntactically valid entity-tag value previously received from the same semantic endpoint.

Weak ETags are preserved; M3 does not strip W/ or synthesize validators from payload hashes.

Invalid upstream ETag syntax is retained as evidence metadata where possible but is not replayed as an If-None-Match validator.

## 13. HTTP cache policy

M3 implements a named/versioned policy, initially:

~~~text
warapi-cache-policy@1
~~~

Returned source cache semantics take precedence over a faster local target.

Normal next eligibility:

~~~text
max(
    sourceCacheEligibleAt,
    configuredTargetAt,
    retryEligibleAt
)
~~~

### 13.1 explicit freshness

For cacheable successful representations, follow RFC 9111 explicit freshness:

1. shared-cache s-maxage when present and valid;
2. otherwise max-age;
3. otherwise Expires relative to the response Date when usable;
4. otherwise no explicit source freshness bound.

M3 does not invent heuristic freshness from Last-Modified.

no-cache means the representation may be retained as evidence but must be revalidated before reuse; source-cache eligibility is immediate.

no-store does not erase FoxData audit evidence. It prevents treating that response as a reusable HTTP cache representation/validator in the poll state.

Malformed or conflicting freshness directives fail conservative: they do not justify a later source freshness guarantee.

### 13.2 Age/Date

To avoid revalidating earlier than an origin-declared lifetime, the policy should account for valid Date and Age values when present.

M3 may extend immutable Fetch transport metadata with the minimal fields required to reproduce cache/retry decisions, for example:

- source Date;
- Age seconds;
- raw Retry-After.

The calculated poll-state timestamp remains an operational projection; evidence retains enough inputs to explain the calculation.

### 13.3 304 metadata update

A 304 may update metadata of the retained representation. M3 reconciles validator/freshness fields from the 304 without creating a new body representation.

## 14. Initial cadence before M4 measurement

Initial target cadence is deliberately conservative:

| Capability | Initial local target |
| --- | --- |
| RuntimeWarState | 60 s |
| ActiveMapList | 5 min |
| RegionWarReport | 60 s |
| DynamicMapState | 60 s |
| StaticMapState | first sight + 6 h conditional revalidation |

These are bootstrap hypotheses only.

M4 performs the bounded 48–72 hour measurement and publishes collection-profile@1. A source capability advertising 3-second updates is not permission to poll every active region every 3 seconds.

## 15. Burst control and deterministic spreading

Discovering dozens of regions must not create a synchronized request burst.

M3 planner uses deterministic phase spreading based on stable endpoint identity, for example:

~~~text
offset = StableHash(shardKey, semanticKey, policyVersion) mod cadenceWindow
~~~

Properties:

- restart does not randomize every endpoint into a new phase;
- all regions are not scheduled on a minute boundary;
- multiple workers converge on the same target phase;
- no durable randomness is required.

Initial discovery may spread dynamic/report jobs across the first cadence window and long-cadence static jobs across a larger bootstrap window.

Worker concurrency is bounded independently from schedule phase. MaxConnectionsPerServer is a safety limit, not the primary scheduler.

## 16. Response classification

Every received HTTP response is source evidence, including failures, subject to body safety limits.

| Outcome | Raw capture | Representation effect | Scheduling |
| --- | --- | --- | --- |
| 200 expected JSON | body | promote representation | normal cache/target |
| 200 malformed/incompatible JSON | body | reusable transport representation remains | normal cache/target + parse drift signal |
| 304 | no body + prior representation | retain representation | updated cache/target |
| 3xx | optional bounded body | never follow | failure backoff |
| 400 | optional bounded body | none | long/config backoff |
| 401/403 | optional bounded body | none | operator/config alert + long backoff |
| 404 root endpoint | optional bounded body | none | contract/config alert |
| 404 per-map endpoint | optional bounded body | none | reconcile discovery + slower retry |
| 408/425/429 | optional bounded body | none | Retry-After when valid, else backoff |
| 5xx | optional bounded body | none | bounded backoff |
| transport exception after authorization | no Fetch if no response was obtained | none | M2 uncertain deferral/recovery |

M3 does not use EnsureSuccessStatusCode as the control plane because non-2xx responses still need durable evidence and explicit classification.

## 17. Retry-After and backoff

Valid Retry-After is a lower bound. It may be delta-seconds or HTTP-date.

For HTTP failures without a stronger source delay, M3 uses a bounded, versioned backoff policy with jitter.

Initial candidate:

~~~text
15 s -> 30 s -> 60 s -> 2 min -> 5 min cap
~~~

This is a successor-job delay after a completed HTTP response, not an in-process retry.

For a transport exception after exchange authorization, M2 requeues the same logical job through DeferUncertainExchange; the next claim creates a new AttemptId.

The current job AttemptCount may be used to calculate transport-exception backoff without adding a second retry counter.

## 18. Timeouts and cancellation

M3 separates:

- host shutdown cancellation;
- connect timeout;
- full exchange timeout;
- body-read timeout/deadline.

Because ResponseHeadersRead completes before content has been read, the full exchange deadline must remain active through bounded body streaming.

Use TimeProvider for testable deadlines and elapsed-duration measurement.

After authorization:

- timeout/network cancellation is an uncertain exchange outcome unless a complete response has already been captured;
- host shutdown must not falsely classify an authorized attempt as pre-exchange;
- if graceful persistence cannot run because the host token is cancelled, M2 expiry recovery is the correctness fallback.

Before authorization, deterministic request/configuration failures use DeferBeforeExchange.

## 19. Bounded headers and bodies

M3 must enforce limits at both transport and application layers.

Required configurable bounds include:

- maximum response-header bytes;
- maximum wire/content bytes captured;
- maximum decoded bytes supplied to JSON parsing;
- maximum decompression expansion ratio;
- maximum JSON depth;
- maximum string/property length where practical;
- maximum collection counts used for derived discovery.

Content-Length is only an early rejection hint. Streaming still reads through a bounded sink and detects limit + 1 bytes.

A body larger than the evidence limit must never be allocated in full merely to discover that it is oversized.

Raw response bodies are never logged.

## 20. Content encoding and media type

Expected successful media type is JSON.

M3 should accept application/json and compatible application/*+json. Missing or unexpected media type is preserved as a diagnostic and MUST NOT prevent raw evidence capture.

Parsing policy is deliberately more tolerant than canonical acceptance:

- bounded valid JSON may still be parsed when an otherwise trusted official endpoint has missing or mislabelled JSON media type;
- the media-type drift remains observable;
- canonical acceptance is not decided in M3.

If encoded content must be decoded for parsing:

1. capture encoded content bytes first;
2. decode through a bounded streaming decoder;
3. reject unsupported, multiple or unsafe encodings from semantic parsing;
4. keep the raw Fetch regardless.

## 21. Source DTO strategy

M3 uses System.Text.Json with generated metadata context for known source DTOs.

Use:

~~~text
strong DTO shape
+ raw/open scalar values
+ JsonExtensionData
+ exact raw payload retained independently
~~~

Closed C# enums are forbidden for upstream values that may expand.

Examples:

- winner: preserve raw string;
- teamId: preserve raw string;
- iconType: preserve raw integer;
- flags: preserve raw bitmask;
- unknown JSON properties: preserve in extension data for parsed diagnostics.

Known-value helpers may classify familiar values but never replace the raw value.

The official README currently lists map icon codes only through 92, while upstream issue #137 documents live iconType=97 and a viewDirection field. M3 therefore treats both taxonomy and object shape as open.

## 22. Parsing outcomes

Parsing is a derived step after raw durability.

Suggested outcomes:

~~~text
parsed
parsed_with_unknowns
malformed_json
incompatible_shape
unsafe_identifier
unsupported_content_encoding
body_limit_exceeded
skipped_no_body
~~~

A source 200 whose JSON cannot be parsed is not converted into a transport failure and does not trigger another immediate HTTP request.

The bytes already exist and can be reprocessed by a future parser version.

## 23. Structural fingerprint

M3 implements:

~~~text
json-shape@1
~~~

Purpose: detect source-contract shape drift independently from payload-value changes.

Algorithm requirements:

- stream/parse JSON with bounded depth;
- ignore object property order;
- ignore array element order for shape purposes;
- include property paths;
- include JSON token/value kind;
- distinguish integer-compatible number from non-integer number where useful;
- include observed union kinds when array items differ;
- detect duplicate object property names as a diagnostic;
- sort canonical shape tokens ordinally;
- SHA-256 the canonical UTF-8 token stream.

Examples:

~~~text
ordinary ownership value change -> same shape
array reorder                  -> same shape
new viewDirection property     -> different shape
number -> string               -> different shape
missing optional field         -> potentially different observed shape
~~~

Fingerprint changes are signals, not automatic quality rejection.

## 24. Durable source parse runs

M3 introduces versioned derived parse evidence distinct from later canonical normalization.

Suggested table:

~~~text
evidence.source_parse_runs

id
representation_fetch_id
capability_key
adapter_version
parser_version
fingerprint_algorithm
structural_fingerprint
outcome
unknown_property_count
unknown_code_count
error_code
started_at
completed_at
created_at
~~~

Recommended uniqueness:

~~~text
UNIQUE(representation_fetch_id, capability_key, parser_version)
~~~

A 304 does not create another parse run solely because it revalidated the same representation.

Later M5 normalization remains a separate versioned stage. M3 must not rename source parsing into canonical normalization.

## 25. Parse ordering

Correct ordering:

~~~text
HTTP response
    -> bounded exact raw capture
    -> raw COMMIT
    -> parse / fingerprint
    -> parse-run COMMIT
~~~

Incorrect ordering:

~~~text
HTTP response
    -> parse first
    -> save only if parse succeeds
~~~

A crash after raw capture but before parse must be recoverable without source I/O.

Planner/reconciliation therefore scans for body-bearing current representations that lack the current parser-version run and reprocesses them locally.

## 26. Durable successor planning

A successful raw capture completing a CollectionJob must not be the only trigger for creating the next job.

Otherwise this failure is possible:

~~~text
Fetch COMMIT succeeds
process crashes
successor enqueue never happens
endpoint becomes silent forever
~~~

M3 makes successor planning reconstructible.

Suggested deterministic key:

~~~text
after-fetch:{FetchId}
~~~

Planner rule:

~~~text
for every enabled/discovered endpoint:
    read newest current Fetch
    if endpoint_poll_state has not processed that Fetch:
        classify response
        update poll state
        enqueue successor with deterministic key
~~~

Poll-state update and successor enqueue SHOULD occur in one short PostgreSQL transaction in the M3 scheduling store.

If that transaction has unknown commit outcome, the deterministic key and last_processed_fetch_id make replay observable and idempotent.

No database transaction spans HTTP I/O.

## 27. Normal scheduling versus attempt retry

Keep the two mechanisms separate.

Normal cycle:

~~~text
Job A
 -> successful or known HTTP response
 -> Fetch A
 -> Job A completed
 -> planner creates successor Job B
~~~

Uncertain transport failure:

~~~text
Job A
 -> Attempt 1 authorized
 -> network outcome uncertain
 -> same Job A deferred
 -> later claim
 -> Attempt 2 with new AttemptId
~~~

Do not create a new normal successor while the old logical job is still in an uncertain/retry lifecycle.

## 28. Worker topology

M3 keeps a single Worker process and PostgreSQL queue. No broker is added.

### 28.1 Recovery loop

Calls M2 bounded recovery periodically.

Purpose:

- expired claim without attempt -> requeue;
- pre-exchange expired attempt -> safe requeue;
- authorized unknown result -> uncertain;
- committed Fetch with incomplete workflow -> repair.

### 28.2 Planner/reconciler loop

Idempotently:

- bootstraps source/shards/root endpoints;
- processes new current Fetches into poll state;
- creates successor jobs;
- reconciles map discovery;
- repairs missing source parse runs;
- repairs missing successor jobs.

### 28.3 Executor loop

Only:

~~~text
Claim
BeginAttempt
AcquireFence
BuildRequest
AuthorizeExchange
Send once
Capture
~~~

It does not directly mutate canonical state.

## 29. Configuration

Suggested configuration shape:

~~~json
{
  "WarApi": {
    "Enabled": false,
    "EnabledShards": ["live-1"],
    "EnableDev": false,
    "Http": {
      "ConnectTimeoutSeconds": 10,
      "ExchangeTimeoutSeconds": 30,
      "MaxConnectionsPerServer": 8,
      "MaxResponseHeadersKiB": 32,
      "MaxWireBytes": 8388608,
      "MaxDecodedBytes": 16777216,
      "MaxExpansionRatio": 20
    },
    "Planner": {
      "Concurrency": 4,
      "ReconcileIntervalSeconds": 15
    },
    "Policy": "bootstrap@1"
  }
}
~~~

Exact default byte/time limits are implementation details to validate against fixtures and then M4 measurements. Configuration validators fail closed on nonsensical or unsafe values.

Production configuration does not accept custom source hosts.

## 30. Observability

M3 adds source-level telemetry while reusing OpenTelemetry.

Metrics should include:

- source request outcomes;
- 200/304 ratio;
- source latency;
- wire bytes;
- decoded bytes when applicable;
- poll lag;
- cache-delay seconds;
- Retry-After use;
- transport uncertainty count;
- body/header limit hits;
- parser outcomes;
- unknown code/property counts;
- structural fingerprint changes;
- planner repair count;
- successor scheduling lag.

Low-cardinality metric dimensions only:

~~~text
source
environment
shard
capability
outcome
policy_version
~~~

Do not use map name, FetchId, AttemptId, ETag, payload hash or war ID as broad metric labels.

Those belong in structured logs/traces.

Trace shape:

~~~text
planner -> claim -> attempt -> fence -> authorize
        -> http client span
        -> raw capture
        -> parse/fingerprint
        -> poll-state/successor reconcile
~~~

## 31. Failure modes that M3 must prove

### F1 — HTTP handler silently retries at application layer

Failure: one durable AttemptId produces multiple FoxData-issued sends.

Prevention: no retry/hedging handlers; one explicit send call.

What next if it occurs? Treat as invariant violation, disable ingestion and fix transport composition before reactivation.

### F2 — 503 becomes validator basis

Failure: an error representation ETag causes later 304 and hides recovery of the real resource.

Prevention: only successful reusable representations are promoted as validator basis.

What next? Clear invalid validator state and perform an unconditional new Attempt.

### F3 — 304 points to a prior 304

Failure: M2 rejects priorFetchId because it has no Payload.

Prevention: poll state carries the body-bearing representation Fetch separately.

What next? classify orphan or inconsistent lineage and schedule unconditional refresh.

### F4 — process dies after Fetch COMMIT

Failure: endpoint is never scheduled again.

Prevention: planner reconciles Fetch -> poll state -> deterministic successor.

What next? restarted planner repairs the missing successor without source replay.

### F5 — all map endpoints start simultaneously

Failure: startup/request burst against official service.

Prevention: deterministic phase spreading + bounded executor connections.

What next? planner delays remain durable; M4 measures actual acceptable cadence.

### F6 — upstream adds icon or field

Failure: closed enum/DTO rejects otherwise valid payload.

Prevention: raw/open codes + extension data + structural fingerprint.

What next? raw evidence remains available; taxonomy/parser can be updated and reprocessed.

### F7 — map name normalization changes identity

Failure: MarbanHollow becomes a fabricated MarbanHollowHex endpoint or asset alias.

Prevention: exact opaque source identifier.

What next? aliasing belongs to a separate semantic/reference layer, never source identity.

### F8 — body stream never completes

Failure: lease is held indefinitely because headers arrived before content stalled.

Prevention: full exchange deadline extends through body read.

What next? authorized attempt becomes uncertain/deferred or is recovered after lease expiry.

### F9 — compressed bomb or oversized payload

Failure: high memory/CPU use before evidence bound is enforced.

Prevention: bounded wire reader, bounded decoder, expansion-ratio limit.

What next? raw metadata/error is retained where safe; parse is rejected; operator metric/alert fires.

### F10 — graceful shutdown cancels persistence

Failure: authorized request outcome is not classified before process exits.

Prevention: M2 recovery is the authoritative fallback; graceful best-effort finalization may use a short internal cleanup deadline independent from the already-cancelled host token.

What next? next worker marks/reconciles the expired attempt before sending again.

## 32. Testing plan

Mandatory CI stays fully offline.

### 32.1 source golden fixtures

Cover every documented endpoint plus:

- pre-conquest/null war timestamps;
- unknown winner/team;
- icon 97;
- unknown icon beyond fixtures;
- undocumented viewDirection;
- unknown flag bits;
- unknown additive object property;
- map name MarbanHollow;
- exact-case DeadLandsHex;
- HomeRegion quirks;
- duplicate JSON property;
- duplicate map items/text;
- malformed JSON;
- incompatible field type;
- missing content type;
- unexpected JSON content type;
- unsupported content encoding;
- oversized body;
- decoded expansion overflow;
- near-empty dynamic map;
- mass disappearance;
- mass teamId=NONE;
- historical issue #92/restart fixture.

### 32.2 HTTP behavior tests

Prove:

- exactly one application send per authorized AttemptId;
- no redirects are followed;
- fixed authority is enforced;
- map segment is escaped/validated;
- strong ETag round trip;
- weak ETag round trip;
- invalid ETag not replayed;
- 304 links body-bearing representation;
- repeated 304 still links body-bearing representation;
- orphan 304 schedules unconditional successor;
- 429 Retry-After delta/date;
- 5xx backoff;
- body streaming timeout;
- host cancellation after authorization;
- ResponseHeadersRead body deadline;
- cache Date/Age calculation;
- no-cache/no-store behavior.

### 32.3 PostgreSQL integration

Prove:

- registry bootstrap idempotency/concurrency;
- shard isolation;
- Dev isolation;
- poll-state migration constraints;
- poll state never duplicates fence authority;
- deterministic successor idempotency;
- concurrent planner instances do not duplicate successor jobs;
- crash repair after raw capture;
- parse-run uniqueness;
- parse replay after crash;
- map discovery endpoint idempotency;
- disappeared maps stop receiving new cadence jobs;
- 304 does not duplicate Payload.

### 32.4 fault injection

Inject failures:

- after claim COMMIT;
- after BeginAttempt COMMIT;
- after fence COMMIT;
- after authorize COMMIT before send;
- after response headers;
- mid-body;
- after full body before raw capture;
- after raw-capture COMMIT;
- after raw capture before parse;
- after parse before parse-run COMMIT;
- after parse-run COMMIT before poll-state update;
- after poll-state update/successor transaction unknown outcome.

### 32.5 live canary

A scheduled non-blocking workflow or process may query a bounded subset to detect:

- source contract drift;
- cache-header behavior changes;
- new unknown icon/flag/team codes;
- latency/error changes.

Live canary failure MUST NOT fail mandatory build/test CI.

## 33. Implementation slices

### M3-A — specification and contracts

Deliver:

- this plan;
- focused September 2026 research snapshot;
- ADR-0015 HTTP transport boundary;
- ADR-0016 durable poll state;
- source-abstraction contracts;
- source fixtures and parser/fingerprint tests.

No live traffic.

### M3-B — evidence read model and M3 persistence

Deliver:

- endpoint current/representation read contract;
- ingest.endpoint_poll_state;
- evidence.source_parse_runs;
- migration;
- persistence/integration tests.

No live traffic.

### M3-C — War API parser and fingerprint

Deliver:

- endpoint catalog;
- tolerant DTOs;
- source-generated JSON metadata;
- open-value handling;
- json-shape@1;
- map-name validation;
- regression fixtures including icon 97/viewDirection.

No live traffic.

### M3-D — HTTP transport and policy

Deliver:

- fixed-root HTTP client;
- conditional GET;
- ETag validation;
- RFC-aware cache eligibility;
- Retry-After/backoff;
- bounded body/decoding;
- response classification;
- deterministic transport tests.

Only test transport in mandatory CI.

### M3-E — durable worker orchestration

Deliver:

- recovery loop;
- planner/reconciler;
- executor;
- deterministic spread;
- map discovery;
- crash-safe successor planning;
- parser replay without source I/O;
- OTel source telemetry.

WarApi:Enabled=false remains default until final activation gate.

### M3-F — controlled activation and completion

Deliver:

- controlled one-shard activation procedure;
- Compose/runtime configuration;
- optional non-blocking live canary;
- final offline CI gate;
- completion record;
- M4 measurement handoff.

Do not freeze aggressive cadence here.

## 34. Definition of done

M3 is complete only when all are true:

- all five documented endpoint families are implemented;
- Live-1/2/3 remain isolated;
- Dev is explicit opt-in and isolated;
- production source roots are fixed/allowlisted;
- arbitrary source URLs cannot enter request construction;
- map names remain exact opaque source identifiers;
- ETag/If-None-Match works for strong and weak validators;
- 304 references the body-bearing representation and creates no duplicate Payload;
- orphan 304 self-recovers through an unconditional new Attempt;
- cache eligibility honors explicit source freshness;
- no hidden FoxData retry/hedging/redirect exists;
- one AttemptId issues at most one application send;
- exact pre-content-decoding bytes are hashed/captured;
- body/header/decoded-size limits are enforced;
- raw evidence is durable before parsing;
- unknown properties/codes survive collection;
- icon 97/viewDirection regression fixtures pass;
- structural fingerprints are versioned and persisted;
- source parse runs are idempotent and replayable without HTTP;
- poll state is separate from M2 fence authority;
- successful capture cannot permanently lose its successor job after a crash;
- planner avoids synchronized map fan-out;
- Worker runs M2 recovery in production topology;
- mandatory tests do not use the public Internet;
- live canary is non-blocking;
- no canonical runtime/quality/objective implementation has leaked into M3;
- locked restore, format, Release build, per-project test discovery/execution, contract validation and Compose smoke are green.

## 35. Explicitly deferred

M4:

- 48–72 hour measurement;
- measured cache lifetime distribution;
- actual 200/304 ratio;
- payload/encoding sizes;
- source error/latency distribution;
- final collection-profile@1.

M5+:

- canonical war/shard/region/report model;
- source-to-canonical normalization;
- quality acceptance/rejection;
- issue-92 quarantine semantics;
- taxonomy authority;
- objective identity;
- state intervals/observed changes.

M3 preserves enough evidence to implement all of those without re-fetching historical source responses.
