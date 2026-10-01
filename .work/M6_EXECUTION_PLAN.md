# M6 execution plan — M6-E through M6-H

Status: active execution companion. M6-E is merged through PR #48; M6-F1 is implemented and exact-head verified in PR #49. M6-F2 calibration is next after merge.
Normative specification: M6_MAPS_TAXONOMY_QUALITY.md.
Research evidence: research/M6_MAP_QUALITY_2026-09.md.

This document translates the normative M6 contract into implementation order against the repository state after M6-A through M6-D. If this execution plan and the subsystem specification disagree, the subsystem specification wins.

## Current implementation checkpoint (2026-10-01)

- M6-E2 is on main as #47.
- M6-E3 is on main as #48 with passing post-merge CI/Contracts.
- Both the preliminary ordering reader and the locked quality transaction use the
  same source-evidence chronology query. The preliminary read is advisory only.
- The source-neutral kernel and PostgreSQL store also fail closed when a
  caller attempts to downgrade suspect/quarantined findings to an accepted
  or less severe terminal decision.
- The store refuses an incorrect or omitted latest accepted baseline, checks
  exact source-endpoint/war shard agreement and proven first-seen membership.
- The War API coordinator evaluates the versioned eight-rule structural shell
  after map normalization. Historic restart/mass-NONE rules remain M6-F work.
- The narrowly scoped E3 Worker recovery replays already-normalized,
  body-bearing 200 snapshots missing current-version QualityRuns. Missing
  parse/normalization stages, 304 continuity and versioned reprocessing stay
  explicitly allocated to M6-G.
- The same locked transaction refuses late same-version quality backfill if
  any later terminal result already exists for the WarRegion/capability.
  Such history must be explicitly rebuilt under a new version in M6-G.
- The source-evidence barrier currently scans authoritative body-bearing 200
  candidates; M6-G must extend chronology to any additional 304 quality
  bindings *before* enabling same-war 304 acceptance, so older 304 work cannot
  retroactively change a later baseline.
- E3 cannot close merely because no terminal writes occurred: deferred work
  must remain observable and local recovery must be exercised without HTTP.

## Current baseline

M6-A through M6-D are complete on main.

Frozen foundations unless a concrete defect is found:

- MapSnapshot contracts/kernel/store;
- immutable evidence.map_snapshots;
- immutable evidence.map_item_occurrences;
- immutable evidence.map_text_occurrences;
- quality/run/runtime-map table foundation from M6MapQualityFoundation;
- exact source array ordinal preservation;
- WarApiMapContextResolver;
- WarApiMapSnapshotNormalizationCore;
- static and dynamic map normalizers;
- warapi-static-map-normalizer@1;
- warapi-dynamic-map-normalizer@1;
- warapi-map-taxonomy@1;
- taxonomy schema/profile/interpreter;
- parser-diagnostic versus taxonomy-authority separation;
- M5-H invariants.

The current slice is M6-F1: golden fixtures and deterministic offline feature extraction.

## Non-negotiable invariants

1. Source occurrences are not objective identity.
2. Duplicate source occurrences are preserved.
3. Unknown icon/team/flag/additive values remain representable.
4. Normalization contains no anomaly heuristics.
5. Taxonomy never changes raw source values.
6. Quality never deletes raw or normalized evidence.
7. Static and dynamic remain independent streams.
8. Quality acceptance never fabricates item-level event time.
9. Only accepted quality results create runtime.map_observations.
10. Suspect/quarantined results are terminal quality outcomes but not accepted baselines.
11. Replay/recovery work is versioned.
12. Durable work must recover without a new upstream HTTP request.
13. WarRegion.sourceRegionId may be enriched only through an accepted map observation.
14. M6 does not create ObjectiveId or change events.
15. M5-H remains green after every M6 slice.

# M6-E — Quality kernel and accepted-observation transaction

## E1. Freeze persisted quality contract

Verify current schema against MapQualityContracts.

Required identity:

~~~
mapSnapshotId
+ warRegionId
+ validationFetchId
+ taxonomyVersion
+ qualityPolicyVersion
~~~

Database constraints must enforce:

- decision in accepted/suspect/quarantined;
- completion >= start;
- findings belong to their QualityRun;
- optional occurrence references belong to the same snapshot;
- accepted map observation references the exact run/snapshot/WarRegion/validation Fetch tuple;
- one runtime observation per accepted QualityRun.

If a missing invariant cannot be enforced by the existing foundation migration, add a narrow M6-E migration. Do not rewrite M6-A migrations.

Gate: migration round-trip and all M6-A-D persistence tests green.

## E2. Version quality-policy contract

Add:

~~~
.work/contracts/internal/source/
  warapi-map-quality-policy.schema.json
  warapi-map-quality-policy@1.json
~~~

M6-E policy@1 is the immutable structural-only shell. Once E3 starts persisting QualityRuns, do not add rules or thresholds to that same policy version. M6-F publishes a distinct calibrated policy@2.

Initial rule families:

- region-id.valid;
- region-id.conflict;
- coordinate.valid;
- source-time.representable;
- schema.structure-changed;
- taxonomy.unknown-icon;
- taxonomy.unknown-team;
- taxonomy.unknown-flag-bits.

The profile records policy version, required taxonomy version, rule version, configuration version, effect and deterministic parameters.

Contract workflow must validate the profile.

Do not hide policy parameters as unversioned C# constants.

## E3. Application quality kernel

Implement MapQualityKernel as source-neutral validation/orchestration around IMapQualityStore.

It validates:

- IDs;
- taxonomy/policy versions;
- decision;
- finding rule/version/effect;
- deterministic input metrics;
- occurrence-reference shape;
- timing.

It must not know War API DTOs, PostgreSQL, taxonomy file format or mass-NONE logic.

## E4. PostgreSQL quality transaction

Implement PostgresMapQualityStore.

One transaction:

1. lock target runtime.war_regions row;
2. verify Snapshot, WarRegion and validation Fetch provenance;
3. enforce chronology barrier;
4. select deterministic prior accepted baseline;
5. verify supplied baseline equals DB-selected baseline;
6. insert/get immutable QualityRun;
7. insert findings;
8. if accepted:
   - insert runtime.map_observation;
   - optionally enrich WarRegion.sourceRegionId null -> accepted value;
9. if suspect/quarantined:
   - no runtime map observation;
   - no sourceRegionId enrichment;
10. commit.

Invariant: an accepted QualityRun cannot exist without its map observation; suspect/quarantined cannot have one.

### Concurrency primitive

Use SELECT ... FOR UPDATE on the target WarRegion row for v1.

Reasons:

- process-safe and transaction-scoped;
- no in-process singleton assumption;
- no advisory-lock key/hash design;
- WarRegion already exists as the quality-stream scope;
- static cadence is low and dynamic cadence is measured, so initial coarse per-region serialization is acceptable.

Measure contention before introducing finer locking.

## E5. Chronology barrier

For:

~~~
WarRegion
+ capabilityKind
+ taxonomyVersion
+ qualityPolicyVersion
~~~

candidate C must not become terminal while any earlier authoritative source candidate in the same stream has unfinished work required by the selected versions.

The barrier is evidence-driven, not snapshot-driven. It must detect earlier durable authoritative work at every stage:

~~~
captured_current body-bearing Fetch
    -> current SourceParseRun
        -> current static/dynamic NormalizationRun
            -> MapSnapshot
                -> terminal QualityRun
~~~

An earlier authoritative Fetch with a missing parse, missing normalization, missing snapshot or missing terminal QualityRun blocks C. This is required so a crash between stages cannot let a later candidate permanently choose the wrong immutable baseline.

Ordering:

~~~
source observation boundary ASC
+ one durable deterministic tie-breaker
~~~

The same ordering must be used by:

- source-work barrier;
- baseline selection;
- recovery scanning;
- M6-H rebuild.

The source-work query must use only authoritative captured_current lineage. captured_late/superseded evidence remains evidence but does not block the canonical quality stream.

A prior suspect/quarantined result is terminal for ordering but is never a baseline.

If any earlier authoritative candidate is unresolved:

~~~
current candidate -> Deferred
no QualityRun
no MapObservation
~~~

Required tests:

- chronological A -> B;
- B arriving before A;
- A accepted then B;
- A suspect then B;
- A quarantined then B;
- concurrent B attempts;
- process-death replay;
- independent static/dynamic baseline streams.

## E6. Baseline resolver

Baseline is latest earlier accepted observation for the same WarRegion and capability kind.

Never baseline from:

- suspect;
- quarantined;
- another WarRegion;
- another capability kind;
- a future observation;
- wall-clock processing order.

Persist baselineMapObservationId on each QualityRun.

Null baseline is valid for the first terminal candidate.

## E7. War API quality coordinator

Create a source-composition coordinator such as WarApiMapQualityCoordinator.

Flow:

1. accept normalized MapSnapshotResult and validation boundary;
2. resolve exact M5 WarRegion with WarApiMapContextResolver;
3. select taxonomy version;
4. select quality policy version;
5. interpret raw occurrence values;
6. compute M6-E structural findings;
7. pass result to MapQualityKernel/store;
8. emit low-cardinality telemetry.

War API taxonomy implementation stays outside Core/Application.

## E8. Initial structural effects

Recommended v1 behavior:

- invalid/negative source regionId -> quarantined;
- non-null sourceRegionId conflicting with accepted WarRegion -> quarantined;
- non-finite or documented out-of-range coordinate -> quarantined;
- unknown icon/team/flag bits -> informational or suspect by versioned policy, never automatic quarantine alone;
- structural fingerprint change -> finding, non-blocking when parsing/normalization succeeded.

Never clamp or repair source values.

## E9. Atomic sourceRegionId enrichment

Only accepted quality may mutate WarRegion.sourceRegionId:

- null snapshot value -> no enrichment;
- existing null + accepted value -> set once;
- existing same value -> no-op;
- existing different value -> conflict finding, no acceptance;
- suspect/quarantined -> no enrichment.

Enrichment and accepted observation commit together.

## E10. Runtime integration

After successful static/dynamic normalization:

~~~
SourceParseRun
-> map snapshot normalization
-> map context
-> quality evaluation
~~~

Normalization success must not depend on quality acceptance.

Deferred quality work remains durable for M6-G.

## E11. M6-E telemetry

Add low-cardinality metrics for:

- quality runs by decision;
- finding hits by stable rule key;
- deferred quality work;
- evaluation duration;
- baseline lookup duration;
- sourceRegionId enrichment/conflict.

No map names, UUIDs, coordinates or raw icon values as broad labels.

## E12. M6-E exit gate

M6-E closes only when:

- policy profile is versioned and contract-validated;
- PostgreSQL quality store is complete;
- accepted transaction is crash-safe;
- suspect/quarantine create no runtime observation;
- sourceRegionId enrichment is atomic;
- chronology barrier is executable;
- concurrent/out-of-order tests prove deterministic baseline;
- M5-H remains green;
- Docker migration/readiness/Worker smoke is green.

Do not begin M6-F before this.

## M6-F1 completion checkpoint (2026-10-01)

Delivered and verified before merge:

- versioned `m6-map-quality-fixtures@1` catalog;
- exact upstream warapi#92 restart payload plus provenance-marked minimized/synthetic guards;
- explicit warapi#120, #137, #115, #77 and HomeRegion evidence boundaries;
- deterministic source-specific feature extractor with no anomaly thresholds;
- candidate/baseline source values, exact team/raw-icon distributions, occurrence ratios,
  NONE/owned shares, concentration, progression deltas, taxonomy diagnostics,
  coordinate diagnostics, duplicate counts and null-vs-empty array presence;
- SHA-256 and structural fingerprints in every offline candidate/baseline record;
- offline `FoxData.MapQualityLab` requiring no public War API;
- CI runs the lab twice and requires byte-identical JSONL output;
- source tests prove historical #92 feature shape, no-baseline neutral handling,
  isolated regressions, unknown taxonomy, duplicate occurrence, provenance labels
  and missing-vs-empty array behavior.

Not delivered by F1:

- no thresholds;
- no candidate policy decision;
- no #92 quarantine assertion;
- no `warapi-map-quality@2`;
- no runtime Worker behavior change.

Next slice: M6-F2 offline calibration over these fixtures plus retained M4 evidence,
followed by publication of immutable policy@2 only when false-positive analysis is recorded.

# M6-F — calibrated anomaly policy v2

Current slice: M6-F1 golden fixtures + deterministic offline feature extraction — implementation complete and verified in PR #49. Calibration/thresholds remain M6-F2 and are not part of F1.

## F1. Golden fixture catalog

Minimum fixtures:

1. healthy dynamic baseline;
2. historical warapi#92 restart/mass-NONE regression;
3. healthy recovery;
4. no-baseline war-start neutral state;
5. iconType 97 + viewDirection;
6. duplicate Rocket Target;
7. changed static representation;
8. unknown team;
9. unknown flag bits;
10. invalid coordinate;
11. sourceRegionId conflict;
12. source version regression;
13. lastUpdated regression.

Incomplete upstream issue data must be labelled synthetic/minimized regression derived from the issue.

## F2. Deterministic feature extraction

F1 implementation exposes these measurements as facts only; no thresholds or terminal decisions are encoded here. Raw iconType counts plus a concentration statistic are emitted rather than inventing undocumented icon families.

Extract:

- item/text counts;
- team counts;
- NONE share;
- owned-team share;
- count ratios versus baseline;
- raw iconType distribution as the F1 evidence primitive;
- policy-defined icon-family distribution only if policy@2 explicitly defines a versioned mapping;
- composition concentration/collapse;
- source version delta;
- lastUpdated delta;
- unknown taxonomy counts;
- duplicate occurrence indicators.

No ObjectiveId is required.

F1 freezes the feature math, not anomaly thresholds:

- NONE and owned-team shares use all map-item occurrences as the denominator;
  missing teamId stays in the denominator and has its own count;
- a ratio with a zero baseline denominator is null rather than infinity;
- icon composition is measured from non-null raw iconType counts;
- concentration is sum(p(iconType)^2), emitted without a cutoff;
- raw iconType remains the evidence primitive; grouping icons into a semantic
  family is allowed only when a future versioned policy explicitly defines the mapping;
- source version and lastUpdated deltas use decimal subtraction to avoid overflow;
- duplicate signals group the complete preserved raw occurrence tuple and count
  excess occurrences without deduplicating source evidence;
- missing/null map arrays remain distinguishable from present-but-empty arrays;
- every offline record includes SHA-256 and structural fingerprint for both
  candidate and baseline when a baseline exists.

## F3. Offline calibration

Replay candidate policy against fixtures and retained M4 evidence.

Record:

- feature values;
- rule hits;
- candidate decision;
- false-positive notes.

The calibration harness must run offline and must not require the public War API.

## F4. Publish policy@2 without mutating policy@1

After calibration, publish a new warapi-map-quality-policy@2.json with its own full policy identity. Preserve policy@1 unchanged for deterministic historical reprocessing. The calibrated policy may include the structural rules carried forward from @1, but its entire rule set and thresholds are frozen together.

Mass-NONE must be composite. A high NONE share alone is insufficient.

Signals can include:

- ownership collapse;
- NONE increase;
- item-count collapse;
- icon-family collapse;
- version reset/regression.

## F5. Operational anomaly rules

Implement:

- version regression/gap;
- lastUpdated regression;
- near-empty representation;
- mass disappearance;
- mass teamId=NONE/restart composite;
- duplicate occurrence diagnostic;
- unknown taxonomy diagnostics.

Persist deterministic input metrics with each finding.

## F6. Baseline safety

Required sequence:

~~~
accepted A
quarantined B
accepted C
~~~

must produce:

~~~
C.baseline = A
~~~

This must remain true after local rebuild.

## F7. M6-F exit gate

Prove:

- #92 regression quarantined;
- healthy baseline/recovery accepted;
- legitimate no-baseline neutral state not falsely quarantined;
- unknown icon/team/flags alone do not trigger restart quarantine;
- duplicate source occurrence survives;
- thresholds live in versioned config;
- chronological/concurrent replay deterministic;
- M5-H remains green.

# M6-G — coverage, recovery and reprocessing

## G1. Generalize capability plan

Replace growth of hardcoded capability SQL with a source-neutral plan concept:

~~~
CoverageCapabilityPlan
  CapabilityKey
  ParserVersion
  NormalizerVersion
  DependencyRank
~~~

Infrastructure queries generic candidate sets from supplied plan data.

Worker supplies War API-specific dispatch.

M5 behavior must remain identical.

## G2. Extend coverage to M6 capabilities

Add static-map-state and dynamic-map-state to existing coverage semantics:

- observed;
- source_not_modified;
- source_unavailable;
- collector_unavailable;
- rejected;
- unknown.

Quality is separate. Example:

~~~
coverage = observed
quality = quarantined
~~~

## G3. Local parse repair

For authoritative durable representation bytes with missing parser@1 run:

- decode local bytes;
- replay parser;
- record normal SourceParseRun;
- no new HTTP request.

## G4. Normalization gap scanner

Find authoritative static/dynamic SourceParseRuns without current M6 normalizer run.

Replay source-local normalization before requiring M5 context.

## G5. Quality gap scanner

Find normalized snapshots/validation bindings missing terminal quality under selected taxonomy/policy versions.

Process in the exact M6-E chronology order.

## G6. Same-war 304

304 creates no new:

- Payload;
- SourceParseRun;
- MapSnapshot;
- occurrence rows.

When a quality evaluation is required at the validation boundary, reuse the existing snapshot.

## G7. Cross-war 304

For an unchanged validated representation after WarRegion transition:

1. prove exact 304 -> prior representation lineage;
2. prove new WarRegion with M5;
3. reuse the map snapshot;
4. run taxonomy/quality for new WarRegion + validation Fetch;
5. create accepted observation only if policy accepts;
6. do not duplicate occurrence rows.

## G8. Taxonomy/policy upgrades

New versions create new derived quality runs.

Never rewrite old versioned results.

Reprocessing identity remains:

~~~
MapSnapshot
+ WarRegion
+ validationFetch
+ taxonomyVersion
+ qualityPolicyVersion
~~~

## G9. Recovery tests

Independent RecoveryTests must cover:

- raw Fetch / parse missing;
- parse / normalization missing;
- normalization / quality missing;
- unresolved earlier chronology candidate;
- quality transaction interruption;
- 304 coverage / quality binding missing;
- cross-war 304 binding missing.

## G10. M6-G exit gate

Prove:

- recovery uses no new upstream Fetch;
- 304 duplicates no snapshot/occurrence rows;
- quality recovery converges;
- deferred work stays visible/retryable;
- M5-H remains green after coverage-store generalization;
- completion helpers do not call zero-progress/non-zero-deferred quiescent.

# M6-H — completion gate

## H1. Representative graph

Include:

- two wars;
- WarRegion transition;
- changed static representations;
- multiple dynamic representations;
- accepted baseline;
- suspect representation;
- quarantined restart anomaly;
- accepted recovery;
- unknown icon/team/flag;
- duplicate occurrence;
- 200;
- same-war 304;
- cross-war 304;
- source_unavailable;
- collector_unavailable;
- rejected;
- unknown/uncertain coverage.

## H2. Convergence condition

Completion requires:

~~~
progress == 0
AND deferred == 0
AND outstanding work == 0
~~~

Zero progress with deferred work is failure.

## H3. Bidirectional provenance audit

Forward chain:

~~~
Fetch/Payload
-> SourceParseRun
-> NormalizationRun
-> MapSnapshot
-> QualityRun
-> MapObservation when accepted
~~~

Reverse requirements:

- every MapSnapshot has exact representation provenance;
- every accepted QualityRun has exactly one MapObservation;
- suspect/quarantined has no MapObservation;
- occurrences belong to exact snapshot;
- findings belong to exact run/occurrence;
- 200/304 validation lineage is exact.

## H4. Quality/projection audit

Require:

- snapshot occurrence counts equal child rows;
- source ordinals complete/unique;
- duplicates preserved;
- sourceRegionId enrichment obeys acceptance rule;
- no unresolved earlier chronology candidate;
- stored baseline equals deterministic recomputation;
- baseline chain excludes suspect/quarantined;
- taxonomy/policy versions exist in repository contracts;
- input metrics JSON is deterministic/parseable;
- no fabricated ObjectiveId in M6 state.

## H5. Semantic snapshot

Compare:

- normalized map source fields;
- source occurrence multiplicity/order;
- raw occurrence values;
- taxonomy/policy versions;
- decisions/findings;
- baseline semantic reference;
- accepted observation boundaries;
- durable M2/M3/M5 evidence references.

Exclude internal regenerated UUIDs and local processing timestamps unless later declared public identity.

## H6. Delete only M6-derived state

Preserve M2/M3/M5 authoritative evidence/context.

Delete M6-derived:

- M6 normalization runs in safe FK order;
- map snapshots/occurrences;
- quality runs/findings;
- runtime map observations;
- M6 continuity/reprocessing rows.

## H7. Local rebuild

Rebuild without HTTP.

Assert Fetch/Payload counts unchanged.

## H8. Re-audit and compare

Require:

- zero outstanding work;
- zero provenance violations;
- zero quality/projection violations;
- semantic snapshot equality.

## H9. Performance evidence

M6 completion record must report:

- total tests;
- snapshot count;
- item occurrence p50/p95/p99/max;
- text occurrence distribution;
- rows/bytes;
- rebuild throughput;
- quality evaluation duration;
- baseline lookup duration;
- unknown taxonomy counts;
- suspect/quarantine rate in representative data.

## H10. Completion record

Create .work/M6_COMPLETION.md containing:

- delivered slices/PRs;
- frozen normalizer/taxonomy/policy versions;
- schema ownership;
- golden fixtures;
- recovery proof;
- CI/test evidence;
- deliberate exclusions;
- M7 handoff.

# Slice dependency matrix

| Slice | Requires | Produces | Must not do |
| --- | --- | --- | --- |
| M6-A | M5 | contracts/schema/context | live quality logic |
| M6-B | A | static snapshots | objective identity |
| M6-C | A/B core | dynamic snapshots | anomaly acceptance |
| M6-D | raw normalized values | versioned taxonomy | rewrite raw values |
| M6-E | A-D | quality + accepted observations | freeze uncalibrated anomaly thresholds |
| M6-F | E + fixtures/evidence | calibrated policy@2, frozen structural @1 | objective matching |
| M6-G | E/F | deterministic recovery | new HTTP for durable work |
| M6-H | A-G | completion proof | new domain features |

# Recommended PR sequence

~~~
M6-E1 policy contracts + validation
M6-E2 quality kernel/store + atomic transaction
M6-E3 coordinator + chronology/concurrency tests + Worker integration
M6-F1 golden fixtures + offline feature extraction
M6-F2 calibrated policy@2 + anomaly rules
M6-G1 source-neutral coverage-plan refactor
M6-G2 M6 recovery + 304 continuity
M6-H completion gate + completion record
~~~

Each PR must include affected normative docs, executable tests, no M7/M8 scope creep, format/build and all relevant test/smoke gates.

# Current execution checkpoint

M6-E1: merged PR #45.
M6-E2: merged PR #47.
M6-E3: active implementation PR #48; completion **not claimed** until
full CI, concurrency and local-recovery evidence is green.

Next after E3 acceptance: M6-F1 golden fixtures and offline extraction,
then M6-F2 calibration. Do not move anomaly thresholds into E3.
