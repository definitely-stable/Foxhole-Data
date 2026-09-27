# M5 Canonical War / Region / Report completion record

Status: completed.
Completion date: 2026-09-27.
Predecessor: M4 Source Measurement.
Successor: M6 maps, taxonomy and quality.
Normative milestone specification: [M5_CANONICAL_WAR_REGION_REPORT.md](M5_CANONICAL_WAR_REGION_REPORT.md).

This record closes the M5 milestone after the executable completion gate and its post-completion audit hardening.

## Delivery

M5 was delivered as the canonical war/region/report foundation over immutable M2/M3 evidence:

- PR #25 — M5-A through M5-C: canonical contract, persistence foundation and generic normalization-run kernel;
- PR #31 — M5-D: deterministic war normalization from durable source evidence;
- PR #32 — M5-E: exact region discovery and within-war membership;
- PR #33 — M5-F: immutable war-report normalization;
- PR #34 — M5-G: explicit coverage, local recovery and deterministic reprocessing;
- PR #35 — initial executable M5-H completion gate;
- PR #36 — implementation-plan synchronization;
- PR #37 — completion-gate hardening after audit: fail-closed convergence, zero-outstanding-work proof, bidirectional provenance and projection consistency.

## Canonical model delivered

M5 owns the following canonical/derived model:

~~~text
M2 Fetch / Payload
        |
M3 SourceParseRun
        |
M5 NormalizationRun
        |
        +--> runtime.wars
        |       +--> immutable runtime.war_observations
        |
        +--> runtime.regions
        |       +--> runtime.war_regions
        |
        +--> immutable runtime.war_report_observations

M2/M3 Attempt / Fetch evidence
        |
        +--> evidence.coverage_observations
                |
                +--> evidence.coverage_reprocessing_runs
~~~

War identity is shard-scoped by exact opaque sourceWarId.

Region source identity remains exact and case-sensitive. M5 does not strip suffixes, case-fold source map names, or infer aliases.

WarRegion identity is war-scoped and retains the exact source map name.

War and war-report history is append-only. Mutable runtime rows are bounded rebuildable projections, not historical truth.

## Time semantics delivered

M5 keeps the existing clock separation:

- Fetch retrievedAt remains source retrieval completion time;
- canonical observedAt is derived from the authoritative evidence boundary;
- recordedAt remains local database durability time;
- source lifecycle timestamps are retained only when supplied by the source;
- dayOfWar remains source data and is not promoted to a canonical global clock.

M5 never fabricates an event timestamp between independent source polls.

## Normalization and acceptance

Versioned normalization identity is:

~~~text
(sourceParseRunId, normalizerVersion)
~~~

Delivered normalizers:

~~~text
warapi-war-normalizer@1
warapi-region-normalizer@1
warapi-war-report-normalizer@1
~~~

Accepted war and report writes preserve exact evidence provenance and are atomic with their required immutable canonical observation.

Structurally invalid required identity, unsafe timestamps and negative structural counters may be rejected.

Open/unknown source values that remain structurally representable are preserved rather than silently collapsed into known values.

Taxonomy confidence, objective identity and map anomaly policy remain outside M5.

## Coverage model delivered

Coverage is explicit and independent from domain state:

~~~text
observed
source_not_modified
source_unavailable
collector_unavailable
rejected
unknown
~~~

A missing durable Attempt is not converted into a fabricated coverage record.

A source 503 or collection failure does not imply unchanged canonical state.

For source_not_modified, M5 preserves:

~~~text
304 validation Fetch
    -> exact prior body-bearing representation Fetch
    -> Payload
    -> versioned SourceParseRun
~~~

No duplicate payload or semantic source parse is created solely because validation returned 304.

## Cross-war 304 continuity

M5-G closes the active-map-list 304 boundary without breaking M5-E normalization identity.

When an exact prior map-list representation is validated by 304 across a war boundary, the versioned coverage reprocessor may project the unchanged exact map membership into the WarId proven at the validation boundary.

This operation:

- reuses the original representation bytes and SourceParseRun;
- creates no synthetic Fetch, Payload or source parse;
- creates no duplicate region-normalization run;
- records a versioned coverage_reprocessing_run;
- is required before M5-F may bind a report through that 304 map context.

Current processor identity:

~~~text
warapi-coverage-reprocessor@1
~~~

## Local recovery and reprocessing

M5 recovery requires no new upstream exchange for work already represented by durable evidence.

Verified repair paths include:

- raw current Fetch/Payload committed before SourceParseRun;
- SourceParseRun committed before M5 normalization;
- deferred maps awaiting durable war context;
- deferred war reports awaiting war/map membership context;
- active-map-list 304 continuity awaiting coverage processing;
- deliberate deletion of all M5-derived rows followed by reconstruction from preserved M2/M3 evidence.

The recovery scanner uses the existing M5-D/E/F versioned coordinators and respects authoritative captured_current evidence. Late/fenced-out Fetches do not become current source chronology.

## M5-H executable completion proof

The completion gate creates a representative evidence graph containing:

- two wars;
- exact active-map membership;
- a cross-war active-map-list 304;
- an immutable war report;
- observed coverage;
- source_not_modified coverage;
- source_unavailable coverage;
- collector_unavailable coverage;
- uncertain/unknown coverage;
- malformed/rejected source representation.

It then:

1. runs M5 recovery to fail-closed convergence;
2. requires zero provenance violations;
3. requires zero outstanding M5 work;
4. requires zero bounded-projection violations;
5. captures a semantic M5 snapshot;
6. deletes only M5-derived rows while preserving M2/M3 Attempts, Fetches, Payloads and SourceParseRuns;
7. reruns local M5 recovery without a new source Fetch;
8. repeats all audits;
9. compares the rebuilt semantic snapshot with the original.

Semantic equality includes source/natural identity, source values, evidence identifiers, versioned outcomes and observation boundaries.

Deliberately regenerated internal surrogate UUIDs and local createdAt/recordedAt/processing timestamps are excluded from semantic equality. They are storage identities, not an M5 public identifier contract.

The gate also contains a negative regression proving that zero newly completed work is not accepted as quiescence while canonical work remains deferred.

## Completion invariants verified

The final M5-H hardening proves:

- every terminal M5 Attempt in the representative graph has coverage;
- no authoritative current M5 SourceParseRun remains without its expected terminal normalizer result;
- no active-map-list source_not_modified coverage remains without its current continuity-processing result;
- immutable war/report observations retain exact NormalizationRun -> SourceParseRun -> representation Fetch -> Payload provenance;
- normalized war/report runs resolve forward to their required immutable canonical observation;
- endpoint capability/semantic provenance agrees with the canonical observation type;
- coverage Fetch/representation/parse lineage remains exact;
- coverage reprocessing remains attached to proven source_not_modified 304 lineage;
- runtime.wars first/last bounds are observation-derived;
- runtime.wars projected warNumber follows the documented durable observation ordering;
- WarRegion bounds and source-scoped exact identity remain valid;
- report observations do not predate their proven WarRegion membership;
- completion fails rather than hiding an unresolved deferred dependency;
- M5 canonical Core/Application/Infrastructure code remains consumer-neutral;
- recovery is independently exercised by the RecoveryTests project.

## Final verification gate

Executable hardening head before this completion-record commit:

~~~text
commit:                         443cada8076655c4f36e373f2b862657eb31a659
PR:                             #37
CI run:                         36336658887
Contracts run:                  36336658917
Dependency Review run:          36336658891
Release build:                  success
Compiler errors:                0
Unit tests:                     24 passed / 0 failed / 0 skipped
Integration tests:              87 passed / 0 failed / 0 skipped
Recovery tests:                 15 passed / 0 failed / 0 skipped
Source tests:                   114 passed / 0 failed / 0 skipped
Contract tests:                 4 passed / 0 failed / 0 skipped
Total tests:                    244 passed / 0 failed / 0 skipped
Docker Compose migration job:   success
Schema-aware API readiness:     success
Worker smoke:                   success
Docker Compose smoke:           success
Contracts workflow:             success
Dependency Review:              success
~~~

Mandatory tests use local deterministic fixtures and PostgreSQL/Testcontainers. The M5 completion proof does not require the public Official War API.

This completion-record commit must pass the same repository gates before PR #37 is merged.

## Deliberate M5 exclusions

M5 does not implement:

- static/dynamic map item canonicalization;
- icon/flag taxonomy;
- map anomaly/quality rules such as issue-92 mass-NONE handling;
- objective identity/matching;
- exact state-change event timestamps;
- state intervals or replay queries;
- public canonical HTTP query APIs;
- public stable canonical IDs;
- Chronicle-specific concepts or UI projections.

These are not completion defects. They are explicitly assigned to M6+.

## M6 handoff

M6 may build static/dynamic map normalization, versioned taxonomy and quality decisions on top of M5, but it must preserve the M5 boundaries:

- immutable M2/M3 evidence remains authoritative provenance;
- unknown source values survive until an explicit versioned interpretation decides otherwise;
- coverage gaps remain explicit;
- snapshot observations are not silently converted into exact source events;
- exact source map identity remains distinct from later taxonomy/alias identity;
- new quality rejection cannot erase source evidence;
- M6 derived state must remain reproducible from durable evidence and versioned interpretation.

M6 must not retroactively rewrite M5 war/report history or weaken M5-H completion invariants.
