# M6 execution plan — M6-E through M6-H

Status: active execution companion. M6-E is merged through PR #48; M6-F1 is merged as #49; M6-F2 calibrated policy@2 is complete in PR #50 with runtime activation deliberately deferred to M6-G.
Normative specification: M6_MAPS_TAXONOMY_QUALITY.md.
Research evidence: research/M6_MAP_QUALITY_2026-09.md.

This document translates the normative M6 contract into implementation order against the repository state after M6-A through M6-D. If this execution plan and the subsystem specification disagree, the subsystem specification wins.

## Current implementation checkpoint (2026-10-03)

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
- M6-G1 through G4 are on main through PR #54.
- M6-G5 replaces the narrow E3 pending reader with a version-aware,
  source-neutral quality-gap scanner and a testable recovery coordinator.
  It replays authoritative body-bearing 200 snapshots through the normal
  quality coordinator/store path without creating new upstream evidence.
- M6-G6 extends quality-gap recovery to exact-lineage same-war 304
  validations while reusing the existing MapSnapshot and quality transaction.
  Cross-war 304 remains allocated to G7; versioned rebuild/policy@2
  activation remains G8.
- The same locked transaction refuses late same-version quality backfill if
  any later terminal result already exists for the WarRegion/capability.
  Such history must be explicitly rebuilt under a new version in M6-G.
- The source-evidence barrier now scans authoritative body-bearing 200 and
  exact-lineage 304 validation boundaries in the same
  `retrieved_at + FetchId` chronology. A missing earlier 304 QualityRun
  therefore blocks later baseline advancement.
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

M6-F2 is complete in PR #50. M6-G1 through G5 are complete on main through PR #55, and G6 is implemented in PR #56. The next implementation slice after merge is M6-G7: cross-war 304 quality binding over the already-proven exact representation lineage.

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

M6-F2 offline calibration + immutable policy@2 is complete in PR #50. Runtime policy selection remains @1 until M6-G ordered versioned reprocessing.

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

Implementation/calibration record: [M6_F2_CALIBRATION.md](M6_F2_CALIBRATION.md).

Replay candidate policy against fixtures and retained M4 evidence.

Record:

- feature values;
- rule hits;
- candidate decision;
- false-positive notes.

The calibration harness must run offline and must not require the public War API. It fails closed on an expected-decision mismatch, a missing required rule hit, or a forbidden rule hit. CI runs it twice and requires byte-identical reports.

## F4. Publish policy@2 without mutating policy@1

After calibration, publish a new warapi-map-quality-policy@2.json with its own full policy identity. Preserve policy@1 unchanged for deterministic historical reprocessing. F2 publishes @2 but MUST NOT switch runtime selection until M6-G implements ordered versioned reprocessing; publishing a policy is not permission to silently reinterpret historical snapshots.

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

M6-G1 is complete in PR #51. M6-G2/G3 static/dynamic coverage extension plus local parse repair is complete in PR #52. M6-G4 normalization-gap recovery is complete in PR #54. The next slice is M6-G5 quality-gap scanning; policy@2 activation remains deferred.

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

G1 implementation checkpoint:

- `CoverageCapabilityPlan` is an Application-layer source-neutral value carrying capability key, parser version, normalizer version and dependency rank;
- PostgreSQL candidate selection receives the plan as typed arrays and joins it with `unnest`; no War API capability literals or capability-specific normalizer `CASE` remain in uncovered/canonical candidate SQL;
- Worker composition supplies the legacy M5 plan with dependency ranks war=0, active-map-list=1, region-war-report=2;
- duplicate capability keys, empty/untrimmed plan identities and invalid ranks fail closed;
- RecoveryTests prove plan filtering and supplied dependency ordering independently of source timestamps;
- static/dynamic map capabilities are intentionally absent until G2.

G1 acceptance: exact-head CI, Contracts, Dependency Review, RecoveryTests and Docker smoke are green; M5 behavior remains identical.

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

G2/G3 implementation boundary:

- coverage+parse plan now includes `static-map-state` and `dynamic-map-state`;
- canonical-normalization plan intentionally remains M5-only until G4;
- a crash after durable map capture but before parser@1 is repaired from local representation bytes before coverage classification is finalized;
- successful map recovery records `coverage=observed` and a normal SourceParseRun without issuing a new Fetch;
- no map normalization, quality evaluation or policy@2 activation is allowed in this slice;
- multiple uncovered validation attempts that reuse one body-bearing representation reuse one repaired parser result within the recovery batch instead of repeating local decode/parse work;
- map 304 recovery preserves one Payload/representation/SourceParseRun lineage and records separate `observed` and `source_not_modified` coverage boundaries without normalization.

G2/G3 acceptance: durable static/dynamic captures with missing parser@1 converge to normal SourceParseRun + coverage=observed using only local evidence; 200+304 reuse one representation/parse lineage with source_not_modified coverage at the validation boundary; Fetch/Payload counts do not grow during recovery, normalization remains zero, replay is idempotent, and full repository gates are green.

## G4. Normalization gap scanner

Find authoritative static/dynamic SourceParseRuns without current M6 normalizer run.

Replay source-local normalization before requiring M5 context.

G4 implementation boundary:

- reuse the source-neutral canonical reprocessing query introduced by G1; do not add map-specific SQL;
- canonical normalization plan includes all five War API capabilities, while Worker dispatch remains capability-specific;
- static/dynamic gaps dispatch to the existing transactional map normalizers;
- normalized map recovery commits the existing normalization_run + map_snapshot + occurrence transaction only;
- rejected source parses become terminal rejected normalization runs through the existing normalizer contract;
- recovery uses only durable representation bytes and never performs a new upstream Fetch;
- no new quality run, map observation, taxonomy version or policy@2 activation is introduced by G4 itself.

G4 acceptance:

- an existing static/dynamic SourceParseRun with no current normalizer run converges locally;
- map snapshot and source occurrences are created exactly once;
- 200 + 304 validation lineage still normalizes only the reused body-bearing representation;
- replay is idempotent and Fetch/Payload counts do not grow;
- full repository gates remain green.

G4 acceptance is complete in PR #54: exact-head CI, Contracts, Dependency Review, RecoveryTests and Docker smoke are green.

## G5. Quality gap scanner

Find normalized snapshots/validation bindings missing terminal quality under selected taxonomy/policy versions.

Process in the exact M6-E chronology order.

G5 implementation boundary:

- promote the narrow E3 pending-snapshot reader into a source-neutral, version-aware quality-gap reader driven by the same `CoverageCapabilityPlan` value used by recovery composition;
- the Worker supplies only static/dynamic map capability + parser + normalizer identities selected for the current runtime;
- G5 scans authoritative `captured_current`, body-bearing 200 validation bindings only; same-war and cross-war 304 quality bindings remain G6/G7;
- use `validationFetchId + observedAt` as the recovery cursor identity even though G5 body-bearing 200 currently has validation Fetch == representation Fetch;
- recover through the existing `WarApiMapQualityCoordinator -> MapQualityKernel -> PostgresMapQualityStore` path; recovery never writes quality tables directly;
- the locked store remains the final chronology/baseline authority and rechecks the M6-E ordering plan under the WarRegion row lock;
- ordinary prerequisite gaps remain retryable `deferred`;
- `later_quality_already_terminal` is surfaced separately as version-blocked same-version history and is not silently treated as quiescent;
- runtime selection remains `warapi-map-quality@1`; calibrated policy@2 activation remains G8;
- no parser replay, normalization replay, new Fetch, new Payload, duplicate snapshot or duplicate occurrence is created by G5.

G5 acceptance:

- a durable normalized 200 snapshot with no current selected-version QualityRun converges locally to exactly one terminal quality result;
- accepted recovery creates exactly one runtime map observation; suspect/quarantined recovery creates none;
- repeated recovery is idempotent and all Fetch/Payload/parse/normalization/snapshot/occurrence counts remain unchanged;
- scanner filtering is fail-closed on capability/parser/normalizer versions;
- an existing 304 validation is not quality-bound by G5;
- policy@2 QualityRuns are not created;
- deferred/version-blocked work remains explicitly observable for later passes/M6-H;
- full repository gates and M5-H regressions remain green.

## G6. Same-war 304

304 creates no new:

- Payload;
- SourceParseRun;
- MapSnapshot;
- occurrence rows.

When a quality evaluation is required at the validation boundary, reuse the existing snapshot.

G6 implementation boundary:

- extend the G5 quality-gap scanner to exact-lineage 304 validations where
  `status=304`, the validation has no Payload, `prior_fetch_id` points to
  the selected body-bearing 200 representation for the existing snapshot, and
  the validation boundary is strictly later under the same
  `retrieved_at + FetchId` total order used by quality chronology;
- keep the scanner source-neutral: capability/parser/normalizer versions still
  come from the Worker-supplied recovery plan;
- carry both representation observation time and validation observation time so
  the Worker can prove that the reused representation and the 304 validation
  resolve through the normal M5 map-context path to the same WarRegion;
- if those contexts resolve to different wars/WarRegions, do not evaluate
  quality in G6; surface `cross_war_validation_binding` as explicit deferred
  work for G7;
- extend the M6-E chronology barrier from body-bearing 200 Fetches to all
  authoritative 200/304 validation boundaries. A missing earlier 304 quality
  binding blocks every later candidate under the selected taxonomy/policy;
- for eligible same-war 304, call the existing quality coordinator/store with
  the reused MapSnapshot and the 304 validation Fetch. The normal locked
  transaction remains the only writer of QualityRun/findings/MapObservation;
- runtime policy stays `warapi-map-quality@1`; G6 does not activate policy@2.

G6 acceptance:

- `200 -> 304 -> 304` converges in exact `retrieved_at + FetchId` order;
- each 304 gets its own immutable QualityRun and, when accepted, its own
  MapObservation while reusing the original MapSnapshot and occurrence rows;
- the second 304 cannot pass while the first 304 quality binding is missing;
- repeated recovery is idempotent and creates no Payload, SourceParseRun,
  NormalizationRun, MapSnapshot or occurrence rows for 304;
- any 304 lineage that is not strictly later than its representation under
  `retrieved_at + FetchId` is rejected fail-closed by both scanner and
  ordering provenance checks;
- a cross-war 304 over the same representation creates no G6 QualityRun and is
  reported as deferred to G7;
- policy@2 remains inactive and full repository gates stay green.

## G7. Cross-war 304

For an unchanged validated representation after WarRegion transition:

1. prove exact 304 -> prior representation lineage;
2. prove new WarRegion with M5;
3. reuse the map snapshot;
4. run taxonomy/quality for new WarRegion + validation Fetch;
5. create accepted observation only if policy accepts;
6. do not duplicate occurrence rows.

G7 implementation boundary:

- reuse the G6 exact-lineage scanner and 200/304 chronology barrier unchanged;
- generalize the Worker 304 evaluator so a representation context from War A
  and a validation context from War B are valid when both contexts resolve
  through the normal M5 map-context path for the same source map;
- the validation context is authoritative for the target quality identity:
  `existing MapSnapshot + new WarRegion + 304 validationFetch + selected
  taxonomy/policy`;
- do not create a dedicated cross-war insert path. Route the candidate through
  the existing M6-E ordering/kernel/store transaction;
- if representation and validation resolve to the same WarId but different
  WarRegion identities for the same source map, fail closed as an integrity
  violation rather than treating that state as a war transition;
- M5 membership/continuity remains a prerequisite. A durable cross-war
  map-state 304 encountered before active-map-list continuity is repaired must
  remain deferred and converge on a later local pass without new HTTP;
- quality chronology is WarRegion-scoped. The first quality result in the new
  WarRegion MUST have no baseline from the previous war even when both results
  reuse the same MapSnapshot. Subsequent validations in the new WarRegion use
  only accepted baselines from that new WarRegion;
- remove `cross_war_validation_binding` as a normal deferred classification
  once G7 is active. Remaining deferral must identify a real missing durable
  prerequisite such as war/map-list context or continuity;
- runtime policy remains `warapi-map-quality@1`; G7 does not activate
  policy@2.

G7 acceptance:

- War A map `200` -> War B active-map-list `304` -> War B map-state
  `304` converges entirely from durable evidence after M5 continuity;
- before M5 continuity is applied, the cross-war map-state candidate stays
  visible/retryable and creates no QualityRun;
- after continuity, exactly one selected-version QualityRun is created for the
  new WarRegion using the existing MapSnapshot and the War B validation Fetch;
- if accepted, the first War B MapObservation has
  `baseline_map_observation_id = NULL`, even if War A has accepted history
  for the same snapshot/capability;
- a later same-representation 304 in War B baselines on the latest accepted
  War B observation, never on War A;
- cross-war 304 creates no new map Payload, SourceParseRun, NormalizationRun,
  MapSnapshot, map-item occurrence or map-text occurrence;
- repeated recovery is idempotent;
- G5 body-bearing 200 and G6 same-war 304 behavior remain green;
- policy@2 remains inactive and full repository gates stay green.

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
M6-E3: merged in PR #48 after full CI, concurrency and local-recovery
evidence passed.

M6-F1 is merged and M6-F2 is complete in PR #50. Proceed to M6-G coverage/recovery/versioned reprocessing after merge. Do not activate @2 through the narrow E3 recovery path.
