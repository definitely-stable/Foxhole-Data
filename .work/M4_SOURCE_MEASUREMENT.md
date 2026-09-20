# M4 — Source Measurement

Status: normative implementation plan.
Milestone: M4.
Predecessor: M3 Official War API Adapter.
Successor: M5 canonical war/region/report model.

## 1. Purpose

M4 converts the conservative M3 bootstrap polling assumptions into a measured, versioned source collection profile.

The milestone MUST answer:

- how the official War API actually behaves under bounded collection;
- which per-capability cadence is justified by observed cache and representation-change behaviour;
- whether the serial M3 executor remains sufficient;
- whether inline PostgreSQL raw evidence remains appropriate at measured volume;
- which assumptions remain uncertain after a 48–72 hour observation window.

M4 publishes `collection-profile@1`. It does not implement canonical Foxhole state.

## 2. Non-goals

M4 MUST NOT implement:

- canonical war, region, report, map or objective state;
- quality acceptance/quarantine;
- objective identity;
- historical state intervals or replay;
- public query APIs;
- a War API compatibility facade;
- Redis, Kafka, RabbitMQ, NATS or a second scheduler;
- external object storage without a measured requirement and a follow-up ADR;
- blanket 3-second polling merely because an upstream capability may update that often.

## 3. Architectural boundary

The authoritative measurement dataset is durable FoxData state already produced by M2/M3:

~~~text
sources registry
+ ingestion jobs/attempts
+ immutable evidence.fetches
+ immutable evidence.payloads
+ evidence.source_parse_runs
+ ingest.endpoint_poll_state
+ evidence.source_schedule_decisions
~~~

OpenTelemetry is operational telemetry and a cross-check. It is not the sole source of truth for the final M4 report.

The normal evidence chain remains:

~~~text
source
  -> durable collection attempt
  -> immutable Fetch/Payload evidence
  -> source parse/fingerprint
  -> M4 measurement analysis
  -> collection-profile@1
  -> later M5+ canonical processing
~~~

M4 analysis MUST NOT mutate raw evidence.

## 4. Existing M3 inputs

M3 already persists the primary fields required for M4:

- status code;
- request/response/retrieval timestamps;
- request duration;
- media type and content encoding;
- declared content length;
- exact captured payload byte length and SHA-256;
- source ETag;
- Cache-Control;
- Expires;
- source Date;
- source Age;
- Retry-After;
- body error code;
- parse outcome;
- structural fingerprint;
- unknown property/code counts;
- endpoint poll-state timing and failure streak.

M4 MUST prefer deriving metrics from these durable values instead of duplicating them into a second measurement database.

Because `ingest.endpoint_poll_state` is a current rebuildable projection, M4 additionally persists an append-only scheduling-decision ledger keyed by Fetch:

~~~text
evidence.source_schedule_decisions
~~~

Each row records the policy identity, effective cadence, active/probe flags, cache/retry timing bounds, and the exact successor job when one exists. This is derived measurement evidence, not a second scheduling authority. M2 jobs/attempts/fences remain authoritative for execution ownership.

## 5. Measurement definitions

M4 uses the following terms precisely.

### 5.1 Fetch

One durable source response observation. A Fetch may be body-bearing, bodyless, successful or unsuccessful.

### 5.2 Representation

A body-bearing reusable source representation identified by its Fetch/Payload evidence.

### 5.3 Representation change

A change in exact source payload SHA-256 between consecutive body-bearing representations of the same endpoint.

This is deliberately not called a canonical semantic change.

### 5.4 Duplicate 200

A successful body-bearing HTTP 200 whose exact payload SHA-256 equals the immediately preceding body-bearing representation for that endpoint.

### 5.5 Validation hit

A 304 response linked to a known body-bearing representation.

### 5.6 Effective poll interval

The observed interval between application-issued source requests for the same endpoint.

A longer interval than the local target is not automatically scheduler lag: source cache eligibility and retry eligibility are legitimate lower bounds.

M2 collection_jobs.available_at is mutable when an attempt is deferred and requeued. M4 therefore MUST NOT present the current job available_at as an immutable per-attempt historical timestamp. Exact scheduler-lag analysis uses immutable scheduled_for where appropriate, AttemptNumber/idempotency context, Fetch/poll-state evidence and clearly reports cases where retry eligibility can only be reconstructed or bounded.

## 6. Metrics that MUST be measured

The final report MUST provide, where applicable, values by shard and capability.

### 6.1 Cache behaviour

Measure:

- Cache-Control presence;
- max-age and s-maxage values;
- no-cache and no-store occurrence;
- Expires behaviour;
- Date and Age behaviour;
- explicit source freshness lifetime;
- effective source cache delay;
- malformed/conflicting cache metadata.

Report at least p50/p90/p95/p99 and max for useful numeric distributions.

### 6.2 ETag and validation behaviour

Measure:

- ETag prevalence;
- strong vs weak validators;
- invalid/unusable validator occurrence;
- HTTP 200/304 ratio;
- validation-hit ratio;
- duplicate-200 ratio;
- same ETag with different payload hash;
- different ETag with identical payload hash.

The first anomaly is treated as a high-severity source-integrity signal but does not rewrite evidence.

### 6.3 Payload and encoding

Measure:

- captured payload size p50/p90/p95/p99/max;
- declared Content-Length vs actual captured bytes;
- Content-Encoding prevalence;
- decoded size when safely reproducible using the M3 decoder policy;
- decoded/wire ratio when encoding is observed.

M4 does not store duplicate decoded payloads.

### 6.4 Source change behaviour

For each endpoint/capability measure:

- representation-change intervals;
- unchanged 304;
- unchanged 200;
- changed 200;
- structural fingerprint changes.

For map payloads also measure:

- version increments;
- version gaps;
- version regressions;
- lastUpdated regressions where available.

A version jump proves that intermediate upstream versions existed; it does not prove their exact event times.

### 6.5 Latency and failures

Measure:

- request duration p50/p90/p95/p99/max;
- latency by response class;
- HTTP status classes;
- body limit/read errors;
- transport-uncertain attempts;
- capture uncertainty;
- pre-exchange failures;
- Retry-After prevalence and values.

Transport/network uncertainty MUST remain distinct from an upstream HTTP error response.

### 6.6 Scheduling and burst shape

Measure:

- actual per-endpoint poll interval;
- local target delay;
- source-cache extension;
- retry extension;
- scheduler/executor lag where derivable;
- request starts per 1s, 5s, 10s and 60s buckets;
- mean/p95/p99/max short-window request-start rate.

Average RPS alone is not sufficient.

### 6.7 Storage and database growth

Measure before/after the live campaign:

- database size;
- evidence.fetches total/table/index/TOAST size;
- evidence.payloads total/table/index/TOAST size;
- evidence.source_parse_runs size;
- evidence.source_schedule_decisions size;
- ingestion jobs/attempts size;
- Fetch rows/day;
- unique Payload rows/day;
- source parse runs/day;
- payload deduplication ratio;
- physical database growth/day;
- projected 30-day and one-year growth.

M4 MUST explicitly conclude whether ADR-0010 remains valid.

## 7. Executor-capacity measurement

The M3 worker is intentionally serial at the application-exchange level even though the HTTP handler has a higher connection ceiling.

M4 starts from:

~~~text
executorConcurrency = 1
~~~

Do not increase concurrency merely because `MaxConnectionsPerServer` is greater than one.

Concurrency may be increased only when measured queue/scheduling lag shows that the selected profile cannot be serviced with sufficient headroom.

A useful decision input is:

~~~text
required service load ~= sum(
  endpointCount(capability)
  * p95RequestDuration(capability)
  / targetCadence(capability)
)
~~~

The final profile MUST record the chosen executor concurrency.

## 8. High-resolution probe requirement

The normal M3 fleet cadence cannot by itself establish how many short-lived source representations a 60-second poll misses.

M4 therefore MAY use a bounded deterministic probe cohort after the analyzer and profile abstraction are implemented.

The first profile candidate is:

~~~text
maximum probe maps per shard: 3
dynamic-map-state local target: 15 s
region-war-report local target: 15 s
~~~

This is a local target only. The existing rule remains authoritative:

~~~text
nextEligibleAt =
  max(
    sourceCacheEligibleAt,
    configuredTargetAt,
    retryEligibleAt
  )
~~~

The probe MUST NOT bypass Cache-Control/Expires/Retry-After.

Probe selection MUST be deterministic from stable inputs such as:

~~~text
measurementRunId
+ shardKey
+ endpoint semanticKey
~~~

Do not hand-pick only active or quiet regions.

Dev is excluded from the live M4 campaign.

## 9. Downsampling analysis

For capabilities observed through a high-resolution cohort, the analyzer MUST be able to compare candidate cadences such as:

- 15 seconds;
- 30 seconds;
- 60 seconds;
- 120 seconds.

For each candidate estimate from the observed probe series:

- representation episodes retained;
- observed representation capture ratio;
- simulated request count;
- version-gap behaviour where source version is available;
- approximate observation delay.

Downsampling is a counterfactual state-observation simulation, not a counterfactual HTTP cache simulation. A client polling at a different cadence would carry a different validator history, so M4 MUST NOT infer a synthetic 200/304 or duplicate-200 ratio from selected high-resolution responses. HTTP 200/304 and validator efficiency are reported from traffic that was actually observed under the profile that generated it.

The profile should prefer the slowest cadence that still preserves the required source-observation fidelity and freshness while avoiding unnecessary source load.

A 95% observed representation-capture ratio may be used as one engineering input, not as the sole acceptance rule.

## 10. Static map data

A 48–72 hour measurement cannot prove that static map data never changes during an entire World Conquest.

M4 therefore MUST NOT redefine static data as immutable.

If no static change is observed, long-cadence conditional revalidation remains appropriate.

If a static representation does change, the report records it and the measured profile may shorten revalidation.

## 11. Collection profile contract

M4 introduces a versioned operational contract:

~~~text
collection-profile@1
~~~

It owns measured source scheduling values and is distinct from:

- `warapi-cache-policy@1`;
- `warapi-backoff@1`;
- structural fingerprint versions;
- parser versions.

The initial in-code bootstrap profile MUST preserve the exact M3 behaviour before any measured profile is adopted.

The contract contains at least:

- version;
- source key;
- cadence by capability;
- discovery window by capability when different;
- executor concurrency;
- measurement reference/limitations.

Future changes publish a new profile version rather than silently rewriting `@1`.

## 12. Reproducible analyzer

M4 introduces internal measurement tooling, not the future public FoxData CLI.

Preferred location:

~~~text
tools/FoxData.SourceMeasurement/
~~~

The tool is read-only with respect to FoxData evidence and MUST NOT call the upstream War API.

It accepts explicit:

- measurement run identifier;
- start timestamp;
- end timestamp;
- PostgreSQL connection supplied through normal configuration/secret handling.

It produces aggregated outputs such as:

~~~text
measurement-manifest.json
measurement-summary.json
measurement-report.md
storage-before.json
storage-after.json
~~~

Raw source payloads are not committed to Git.

Implemented tool:

~~~text
tools/FoxData.SourceMeasurement/
~~~

The tool never accepts a connection string on the command line. Supply it through `ConnectionStrings__FoxData` or `FOXDATA_CONNECTION_STRING`.

Before the campaign:

~~~text
dotnet run --project tools/FoxData.SourceMeasurement -- storage \
  --label before \
  --output <measurement-directory>
~~~

After the campaign:

~~~text
dotnet run --project tools/FoxData.SourceMeasurement -- storage \
  --label after \
  --output <measurement-directory>

dotnet run --project tools/FoxData.SourceMeasurement -- analyze \
  --run-id <stable-run-id> \
  --start <ISO-8601-with-offset> \
  --end <ISO-8601-with-offset> \
  --output <measurement-directory> \
  --observer-region <coarse-non-secret-region> \
  --repository-sha <exact-commit-sha> \
  --profile-version <collection-profile-version>
~~~

When both storage snapshots exist, `analyze` also emits physical PostgreSQL growth/day and 30/365-day projections in the summary/report.

## 13. Measurement run manifest

Every live run MUST record enough context to make analysis reproducible:

- measurement version;
- run id;
- start/end;
- repository commit SHA;
- adapter/parser/cache/backoff/profile versions;
- enabled live shards;
- campaign phases;
- relevant bounded configuration;
- observer/deployment region in non-secret coarse form.

Do not include credentials or connection strings.

## 14. Recommended live campaign

Preferred duration: 72 hours.
Minimum acceptable duration: 48 hours if operational constraints require it.

### Phase 0 — preflight

Require:

- migration bundle applied;
- mandatory repository gates green;
- recent live canary success;
- dedicated measurement database/volume or otherwise clearly bounded data window;
- Worker starts with source ingestion disabled;
- sufficient disk capacity;
- exact code SHA recorded.

### Phase 1 — Live-1 baseline

Approximate duration: first 6 hours.

Use the unchanged conservative bootstrap profile:

- runtime war: 60 s;
- map list: 5 min;
- war report: 60 s;
- dynamic map: 60 s;
- static map: 6 h.

### Phase 2 — bounded Live-1 probe

Approximate duration: next 6 hours.

Keep the baseline fleet and activate only the bounded deterministic probe cohort.

### Phase 3 — add Live-2

Approximate window: hour 12 through hour 24.

Do not simultaneously change global cadence.

### Phase 4 — add Live-3

Approximate window: hour 24 through hour 72.

This gives Live-3 roughly 48 hours in the preferred campaign.

## 15. Hold/rollback conditions

Stop expansion or disable ingestion if there is a credible operational failure such as:

- persistent 401/403;
- root endpoint 404;
- persistent 429;
- sustained 5xx burst;
- header/body limits being hit unexpectedly;
- material uncertain-exchange burst;
- unbounded queue/scheduling lag;
- database disk pressure;
- source drift that makes the current parser unsafe.

Rollback remains:

~~~text
WarApi__Enabled=false
~~~

Disabling collection never deletes evidence.

## 16. Implementation slices

### M4-A — specification and contracts

Deliver:

- this normative plan;
- collection profile schema/contract location;
- metric definitions;
- live run protocol;
- decision/rollback rules.

No source traffic changes.

### M4-B — collection profile abstraction

Deliver a versioned profile object and refactor hard-coded M3 cadence/discovery values to use it.

The bootstrap profile MUST be behaviour-preserving.

### M4-C — reproducible analyzer

Deliver read-only measurement queries/calculations and deterministic tests using synthetic evidence histories.

### M4-D — bounded probe mode

Only after M4-B/C, add disabled-by-default deterministic probe cohort support that cannot bypass source cache or retry eligibility.

### M4-E — 48–72 hour live campaign

Run the controlled campaign and freeze the exact analysis window.

### M4-F — publish measured profile

Publish:

- research report;
- `collection-profile@1`;
- storage decision;
- executor-concurrency decision;
- limitations;
- M4 completion record.

Then mark M5 as next.

## 17. Required tests

Unit/source tests MUST prove:

- bootstrap profile exactly preserves M3 cadence;
- every required War API capability has a profile entry;
- cadence/discovery values are positive;
- profile version is explicit;
- cache eligibility still overrides a faster profile target;
- Retry-After still overrides a faster profile target;
- probe selection is deterministic and bounded;
- probe mode is disabled by default;
- probe cannot enable Dev implicitly;
- duplicate-200 detection is correct;
- ETag anomaly classification is correct;
- percentile calculations are deterministic;
- representation-change intervals are endpoint-local;
- version-gap/regression detection is correct;
- downsampling calculations are deterministic.

Integration tests SHOULD construct synthetic PostgreSQL histories containing combinations such as:

~~~text
200 body A
304 -> A
304 -> A
200 body B
200 body B
500
transport uncertainty
200 body C
~~~

and assert exact report counts.

All M2/M3 correctness and recovery tests remain mandatory.

## 18. Definition of done

M4 is complete only when:

- this normative plan is implemented;
- hard-coded bootstrap cadence is behind a versioned collection profile without behavioural regression;
- a reproducible evidence-based analyzer exists;
- a bounded probe path exists or a documented measured reason rejects it;
- at least 48 hours of controlled live measurement are complete;
- cache, ETag, response status, latency, size, change and storage behaviour are measured;
- short-window burst shape and executor capacity are measured;
- PostgreSQL growth is measured;
- ADR-0010 is explicitly retained or superseded by a measured follow-up decision;
- `collection-profile@1` is published;
- the Worker uses the published profile;
- M4 completion is recorded;
- M5 becomes the next milestone.

## 19. M5 handoff

M5 consumes source evidence collected under a measured collection profile.

M5 MUST still treat coverage gaps and observation timing honestly: M4 improves collection fidelity but does not turn snapshot observations into exact source event timestamps.
