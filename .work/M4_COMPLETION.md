# M4 Source Measurement completion record

Status: completed.
Completion date: 2026-09-24.
Final implementation commit before this completion-record PR: `686f98f13df1958b490ee7a130e6196304f2cd39`.
Predecessor: M3 Official War API Adapter.
Successor: M5 canonical war/region/report model.

This record closes the milestone defined by [M4_SOURCE_MEASUREMENT.md](M4_SOURCE_MEASUREMENT.md).

## Delivery

M4 converted the conservative M3 bootstrap assumptions into measured source characteristics and an operator-selectable collection-policy model without introducing canonical Foxhole state.

Delivered:

- durable append-only scheduling-decision evidence;
- reproducible PostgreSQL-backed source measurement tooling;
- deterministic bounded Live-1 high-resolution probe mode;
- 48-hour active-observation campaign with checkpoint/recovery continuity;
- measured cadence/downsampling trade-off analysis;
- optional `collection-profile@1` recommendation;
- runtime `collection-policy@1` selection: `bootstrap | recommended | custom`;
- deterministic identity for custom collection policies;
- mandatory safety envelope independent of operator cadence;
- measured executor-capacity and PostgreSQL growth decisions;
- explicit terminal shutdown-tail classification in publication validation.

The bootstrap preset remains the default. The measured recommended preset is opt-in. Custom cadence remains operator-controlled inside the non-disableable source safety envelope.

## Authoritative campaign

Campaign identity:

~~~text
campaign run id:       m4-ci-35604611891
collector repository:  11ce905035b094832ddc18d524b4b0a5b9483724
active segments:       s01..s12
active observation:    173,409 s = 48.169 h
probe:                 Live-1, three deterministic map regions, 15 s target
probe duration:        8 active hours
Dev:                   excluded
~~~

The original hosted run completed s01-s02, then exposed a PostgreSQL init/final-server readiness race before s03. The campaign was resumed from the durable s02 checkpoint after the lifecycle fix; completed observation was not recollected.

The resumed run completed s03-s12 and preserved the original campaign identity and collector data-plane SHA.

## Publication validation

Final offline reanalysis:

~~~text
workflow run:          35975684583
analysis commit:       686f98f13df1958b490ee7a130e6196304f2cd39
source checkpoint:     m4-e-resume-35679449942-s12-checkpoint
Official War API IO:   none
evidenceComplete:      true
errors:                0
~~~

Warnings retained rather than hidden:

- 2 final-window Fetches were durably captured during host shutdown before reconciliation recorded a scheduling decision;
- 53 source-version regressions were observed;
- 1 uncertain exchange was observed.

The two terminal unreconciled Fetches were independently diagnosed as successful `completed / captured_current` responses started less than one second before the final campaign boundary. They are classified separately from interior evidence gaps.

Final scheduling coverage:

~~~text
Fetches:                         317,715
scheduling decisions:            317,713
missing decisions total:         2
terminal unreconciled Fetches:   2
interior missing decisions:      0
~~~

Publication validation remains fail-closed for any unclassified/interior missing scheduling decision.

## HTTP and validator observations

Observed response counts:

~~~text
HTTP 200:  19,911
HTTP 304: 296,168
HTTP 503:   1,636
HTTP 429:       0
HTTP 401/403:   0
~~~

ETag observations:

~~~text
ETag present:                       312,628
strong ETag:                        312,628
weak ETag:                                0
unusable ETag:                            0
same ETag / different payload:            0
different ETag / same payload:            0
~~~

This evidence supports retaining conditional GET/ETag as a primary collection mechanism. It does not convert the upstream source into an event stream or guarantee future validator behavior.

Live-2 and Live-3 root endpoints returned HTTP 503 during the measured expansion phases. They are treated as independent shard availability observations, not as proof that the whole Official War API was unavailable.

## Probe and cadence decision

The 8-hour probe covered three deterministic Live-1 map regions for `dynamic-map-state` and `region-war-report`.

For the observed cohort, 15/30/60/120-second counterfactual downsampling retained all observed representation episodes. Request count decreased approximately linearly with cadence while additional observation delay increased.

Published balanced recommendation:

| Capability | Recommended target | Discovery window |
| --- | ---: | ---: |
| runtime-war-state | 60 s | 60 s |
| active-map-list | 300 s | 300 s |
| region-war-report | 30 s | 30 s |
| static-map-state | 6 h | 5 min |
| dynamic-map-state | 30 s | 30 s |

This is `collection-profile@1`, an optional recommendation rather than a universal requirement or completeness guarantee.

The complete measured trade-off table and limitations are in [M4_COLLECTION_POLICY.md](M4_COLLECTION_POLICY.md).

## Runtime policy model

FoxData now distinguishes:

~~~text
SourceMeasurements
        ↓
SafetyEnvelope        mandatory
        ↓
CollectionPolicy      bootstrap | recommended | custom
        ↓
effective collection schedule
~~~

The effective next request remains bounded by:

~~~text
max(
  operator/preset target cadence,
  source Cache-Control / Expires eligibility,
  Retry-After / durable backoff eligibility
)
~~~

Additional mandatory protections include conditional validation where usable, no in-process retry storm, no hedging, bounded executor concurrency, source/shard controls, and transport admission spacing.

## Executor decision

Measured model:

~~~text
executorConcurrency:          1
modeled endpoints:            165
required serial service load: 0.1568958843
remaining serial headroom:    0.8431041157
minimum modeled concurrency:  1
~~~

M4 therefore retains `executorConcurrency = 1`.

No Kafka, Redis, RabbitMQ, NATS, second scheduler or extra executor concurrency is justified by the measured source workload.

## Storage decision

Measured PostgreSQL physical growth during the campaign:

~~~text
database delta:           431,972,352 B
measured bytes/day:       215,227,648 B
30-day projection:      6,456,829,440 B
365-day projection:    78,558,091,522 B
~~~

Largest measured relation growth contributors included collection jobs, Fetch evidence, attempts and scheduling decisions. Payload storage itself grew materially less because validation hits do not duplicate payload bodies.

ADR-0010's inline PostgreSQL raw-payload decision is retained by ADR-0017. M4 does not justify adding an external filesystem/S3/MinIO CAS durability boundary.

These projections describe the measured campaign workload, not a storage SLA. Retention/partitioning/compaction may still be introduced later when product retention requirements are known.

## Verification

Relevant final gates:

~~~text
PR #23 CI:                 success
PR #23 Contracts:          success
PR #23 Dependency Review:  success
PR #23 offline reanalysis: success
main CI run 35975684612:   success
main Contracts:            success
main reanalysis 35975684583:
  analyze:                 success
  publication validate:    success
~~~

The final reanalysis used only the retained s12 PostgreSQL checkpoint and issued no Official War API traffic.

## Deliberate limitations

M4 does not claim:

- that three measured Live-1 regions represent every future region or war;
- that 30 seconds is the correct cadence for every deployment;
- that source-version gaps equal proven missed externally observable states;
- that Live-2/Live-3 are permanently unavailable;
- that static map state is immutable;
- that snapshot observation times are exact event times;
- that the measured storage projection is a long-term production SLA.

## M5 handoff

M5 is now the next implementation milestone.

M5 consumes M2/M3 evidence under the M4 collection-policy model and implements canonical war/region/report state while preserving:

- source shard/provenance identity;
- observed-at vs source time semantics;
- explicit coverage/gap state;
- raw evidence traceability;
- open/unknown upstream values;
- no assumption that snapshot observations are exact events.

M5 must remain consumer-neutral. No Chronicle-specific UI or product semantics belong in the canonical model.
