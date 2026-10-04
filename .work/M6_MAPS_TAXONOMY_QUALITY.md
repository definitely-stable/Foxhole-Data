# M6 — Maps, Taxonomy and Quality

Status: in progress. M6-A through M6-G5 are complete through PR #55; M6-G6 same-war 304 quality binding is implemented in PR #56. Cross-war 304 remains M6-G7, and policy@2 activation remains deferred to M6-G8.
Prerequisite: M5 Canonical War / Region / Report completed.
Successor: M7 Objective Identity.

M6 turns durable Official War API map representations into reproducible, quality-evaluated map observations without inventing stable objective identity or exact state-change times.

The milestone owns:

- deterministic normalization of `static-map-state` and `dynamic-map-state` source representations;
- immutable source-occurrence snapshots for map items and map text items;
- exact war/region context binding at an authoritative validation boundary;
- versioned icon/team/flag interpretation;
- versioned quality decisions and findings;
- historical anomaly regression behavior, including upstream warapi#92/#120 restart/mass-NONE incidents;
- coverage/recovery/reprocessing extension for static/dynamic map capabilities;
- a deterministic M6 completion rebuild from preserved M2/M3/M5 evidence.

M6 does **not** own:

- stable objective identity or matching between source occurrences;
- cross-war objective identity;
- state intervals;
- exact capture/change event timestamps;
- changeSeq/change-event generation;
- tactical current-map API design;
- Chronicle-specific concepts or UI;
- speculative meanings for undocumented icon/flag values.

Those remain M7+ work.

## 1. Why M6 needs a separate representation layer

The Official War API does not document a stable ID for a map item.

A map payload contains arrays such as:

~~~text
mapItems[]
mapTextItems[]
~~~

Array position is not identity.

Coordinates plus iconType are not identity.

Two records with identical fields are not necessarily one objective. Upstream warapi#115 has documented duplicate Rocket Target entries at the same location, so M6 MUST preserve duplicate occurrences rather than deduplicate by field equality.

Therefore M6 uses four distinct concepts:

~~~text
raw representation
      |
      v
normalized map snapshot
      |
      +--> source item occurrences
      +--> source text occurrences
      |
      v
versioned taxonomy interpretation
      |
      v
versioned quality decision
      |
      v
war-scoped accepted map observation
      |
      v
M7 objective matching
~~~

A **source occurrence** means only "this element existed in this exact representation".

It is not an objective identity.

## 2. Upstream facts that constrain M6

The current Official War API contract documents:

- `/worldconquest/maps/{mapName}/static`;
- `/worldconquest/maps/{mapName}/dynamic/public`;
- common map-level fields `regionId`, `scorchedVictoryTowns`, `mapItems`, `mapTextItems`, `lastUpdated`, `version`;
- item fields `teamId`, `iconType`, `x`, `y`, `flags`;
- normalized coordinates;
- documented map flag bits;
- static data as slow-changing data intended to be requested infrequently;
- dynamic/public data as snapshot data that may change every few seconds;
- ETag / 304 validation semantics.

M6 MUST also retain the stronger defensive rules already established by FoxData:

- static data is not mathematically immutable; upstream warapi#77 documented a static representation changing during a war;
- dynamic state is a snapshot, not an event stream;
- `version` is a source version/cache index, not a timestamp;
- `lastUpdated` is map-level source metadata, not an item event timestamp;
- source map names remain exact opaque case-sensitive identifiers;
- HomeRegionC/HomeRegionW do not currently provide ordinary static/dynamic public map data and their absence is a source capability asymmetry, not an M6 coverage gap by itself;
- iconType is open-ended: the current README list ends at 92 while upstream warapi#137 records iconType 97 and additive `viewDirection`;
- unknown flag bits are preserved but are not assigned invented semantics;
- upstream warapi#92 and #120 document historical restart behavior in which dynamic payloads became truncated and ownership collapsed toward `NONE`, producing false apparent changes;
- upstream warapi#134 documents API/asset naming inconsistencies;
- upstream warapi#115 documents duplicate map occurrences;
- the source does not document a permanent item/objective identifier.

Research evidence for these decisions is summarized in [research/M6_MAP_QUALITY_2026-09.md](research/M6_MAP_QUALITY_2026-09.md).

Concrete implementation ordering for the remaining M6-E through M6-H work is maintained in [M6_EXECUTION_PLAN.md](M6_EXECUTION_PLAN.md).

## 3. Architectural boundary

M6 extends the existing pipeline as:

~~~text
M2 immutable Fetch / Payload
        |
M3 versioned SourceParseRun
        |
M5 war / region context + coverage
        |
M6 versioned map normalization
        |
normalized representation snapshot
        |
versioned taxonomy
        |
versioned quality evaluation
        |
accepted war-scoped map observation
        |
M7 objective identity
~~~

The following distinction is mandatory:

~~~text
"the source returned this item"
!=
"FoxData recognizes the item type"
!=
"FoxData trusts this map snapshot"
!=
"this is objective X"
~~~

M6 owns the first three statements only.

## 4. Representation snapshot versus accepted observation

M6 MUST NOT directly materialize every parsed representation as accepted runtime state.

A body-bearing representation is normalized once into representation-derived rows:

~~~text
evidence.map_snapshots
evidence.map_item_occurrences
evidence.map_text_occurrences
~~~

M6-A has frozen these physical table names and the representation-versus-accepted-observation split. Later slices may extend the schema only when their owned semantics require it; they must not collapse these layers.

A map snapshot is tied to:

- SourceParseRunId;
- NormalizationRunId;
- body-bearing representation Fetch;
- source capability: static or dynamic;
- exact sourceMapName;
- normalized source fields.

It is **not** inherently tied to one WarRegion.

That separation is necessary because a later 304 can prove that the same representation remains current at a new validation boundary, including after a war transition.

After taxonomy and quality evaluation, an accepted war-scoped observation is created:

~~~text
runtime.map_observations
    -> WarRegion
    -> normalized map snapshot
    -> validation Fetch
    -> quality run
    -> observedAt
~~~

For a body-bearing 200:

~~~text
validation Fetch == representation Fetch
~~~

For a validated 304 continuity binding:

~~~text
validation Fetch != representation Fetch
validation Fetch.priorFetchId == representation Fetch
~~~

The normalized snapshot and all source occurrences are reused. M6 MUST NOT duplicate thousands of occurrence rows solely because a 304 validated unchanged bytes.

### 4.1 Normalization is source-local; acceptance is context-bound

A normalized map snapshot is representation-derived evidence and MUST NOT require a resolved WarId/WarRegion merely to exist.

Static/dynamic M6-B/C normalization therefore requires:

- exact source endpoint provenance;
- exact sourceMapName from the endpoint semantic key;
- exact durable representation bytes;
- deterministic parser replay;
- structural normalization invariants.

It does **not** require current M5 war/map membership context.

War/WarRegion context is required later when a validation boundary is evaluated for quality/canonical acceptance.

This separation is mandatory because:

- durable source content must remain locally normalizable even when M5 context is temporarily deferred;
- one normalized representation can be reused by a later 304 validation in another WarRegion;
- source normalization must not be blocked by canonical-context recovery ordering.

`WarApiMapContextResolver`, delivered by M6-A, is therefore primarily an M6-E accepted-binding/quality-context primitive and an M6-G continuity/reprocessing primitive. M6-B/C MAY use it only for optional diagnostics; context resolution MUST NOT be required to commit the normalized representation snapshot.

## 5. Persistence model — M6-A frozen foundation

### 5.1 evidence.map_snapshots

Implemented by M6-A as immutable normalized representation content.

Minimum fields:

- id UUIDv7;
- normalizationRunId;
- sourceParseRunId;
- representationFetchId;
- capabilityKind: `static` or `dynamic`;
- exact sourceMapName;
- sourceRegionId nullable;
- sourceScorchedVictoryTowns nullable;
- sourceVersion nullable;
- sourceLastUpdatedMs nullable;
- sourceUpdatedAt nullable when safely representable;
- sourceMapItemsArrayPresent;
- sourceMapTextItemsArrayPresent;
- itemCount;
- textItemCount;
- recordedAt.

Required uniqueness:

~~~text
normalizationRunId
~~~

The raw millisecond value is retained even when a safe DateTimeOffset conversion is unavailable. M6 quality policy decides whether an invalid/unrepresentable source timestamp blocks acceptance.

### 5.2 evidence.map_item_occurrences

Implemented by M6-A as one row per element in `mapItems` of one exact representation.

Minimum fields:

- id UUIDv7;
- mapSnapshotId;
- sourceOrdinal;
- rawTeamId nullable;
- rawIconType nullable;
- x nullable;
- y nullable;
- rawFlags nullable;
- rawViewDirection nullable.

Required uniqueness:

~~~text
(mapSnapshotId, sourceOrdinal)
~~~

`sourceOrdinal` is a representation-local provenance coordinate only.

It MUST NOT:

- become ObjectiveId;
- be compared across representations as identity;
- be treated as source semantic ordering.

Exact duplicate rows are preserved.

### 5.3 evidence.map_text_occurrences

Implemented by M6-A as one row per `mapTextItems` element.

Minimum fields:

- id UUIDv7;
- mapSnapshotId;
- sourceOrdinal;
- text nullable;
- x nullable;
- y nullable;
- rawMapMarkerType nullable.

Unknown marker types are preserved.

### 5.4 quality.map_quality_runs

Implemented by M6-A as the persistence foundation for one immutable evaluation of one candidate snapshot binding. M6-E will implement evaluation behavior.

Minimum fields:

- id UUIDv7;
- mapSnapshotId;
- WarRegionId;
- validationFetchId;
- taxonomyVersion;
- qualityPolicyVersion;
- baselineMapObservationId nullable;
- decision;
- startedAt;
- completedAt;
- createdAt.

Initial decisions:

~~~text
accepted
suspect
quarantined
~~~

`schema_rejected` remains an externally meaningful quality category, but a structurally unusable representation is represented durably by SourceParseRun / NormalizationRun rejection rather than by inventing a MapQualityRun for a map snapshot that does not exist.

Required evaluation identity:

~~~text
(mapSnapshotId, WarRegionId, validationFetchId, taxonomyVersion, qualityPolicyVersion)
~~~

### 5.5 quality.map_quality_findings

Implemented by M6-A as immutable rule-level evidence storage. M6-E/F will define and execute the versioned rules.

Minimum fields:

- id UUIDv7;
- qualityRunId;
- ruleKey;
- ruleVersion;
- configurationVersion;
- severity/effect;
- optional exact map-item occurrence ID or map-text occurrence ID;
- detailCode;
- deterministic input metrics JSONB;
- createdAt.

The persisted input metrics must be sufficient to explain the decision without reading mutable configuration.

### 5.6 runtime.map_observations

Implemented by M6-A as the accepted-observation binding table. Only a quality policy that allows canonical acceptance may create a row.

Minimum fields:

- id UUIDv7;
- WarRegionId;
- mapSnapshotId;
- qualityRunId;
- validationFetchId;
- capabilityKind;
- observedAt;
- sourceUpdatedAt nullable;
- recordedAt.

Required uniqueness:

~~~text
qualityRunId
~~~

M6 runtime observations are immutable.

No M6 table contains ObjectiveId.

### 5.7 Array-presence preservation — delivered by M6-B

M6-B extends the M6-A snapshot contract and persistence schema with:

- `sourceMapItemsArrayPresent`;
- `sourceMapTextItemsArrayPresent`.

The current tolerant source DTO permits `mapItems` and `mapTextItems` to deserialize as null. Counts alone cannot distinguish a present empty array from a null/missing array, so the shape flags are durable source-derived metadata.

Rules:

- non-null source array => corresponding flag true;
- null/absent source array => flag false;
- flag false requires corresponding occurrence count = 0;
- empty-but-present array remains flag true with count = 0;
- parser-incompatible non-array shapes remain parser rejection and do not create a snapshot.

Both Application validation and PostgreSQL CHECK constraints enforce these invariants.

M6 v1 deliberately does not distinguish explicit JSON null from a missing property at normalization level. Exact raw bytes and the structural fingerprint remain available for a future parser/normalizer version that needs that distinction.

## 6. Storage and indexing constraints

Map occurrence rows are potentially the highest-cardinality M6 data.

M6 therefore starts with narrow indexes only:

- snapshot lookup by NormalizationRunId;
- snapshot lookup by representation Fetch / source parse;
- occurrence lookup by mapSnapshotId + sourceOrdinal;
- quality-run identity;
- runtime observation lookup by WarRegion + capabilityKind + observedAt;
- quality findings by qualityRunId/ruleKey.

Do **not** add broad indexes on every icon/team/coordinate column before measurements justify them.

Do **not** introduce PostGIS in M6. Coordinates remain source-normalized doubles. Coordinate-derived matching belongs to M7 and can justify spatial infrastructure later if measured.

M6-H must report representative occurrence-row counts and query/rebuild cost before any partitioning/columnar/secondary-store decision.

A new storage engine requires evidence and an ADR.

## 7. Map context resolution

Both static and dynamic endpoints are map-scoped but do not include canonical WarId.

The endpoint name alone is not historical membership evidence.

When M6 evaluates a validation boundary for quality/canonical acceptance, it MUST resolve context using the already proven M5 pattern:

1. derive exact sourceMapName from endpoint semantic key:
   - `map-static/<mapName>`;
   - `map-dynamic/<mapName>`;
2. resolve authoritative war evidence at or before the validation boundary;
3. require M5-D normalized WarId;
4. resolve authoritative active-map-list evidence at or before the same boundary;
5. require exact ordinal sourceMapName membership;
6. for a body-bearing active-map-list context, require compatible war context;
7. for a 304 active-map-list context, require an applied current-version M5-G continuity proof;
8. require the exact M5 WarRegion;
9. never fall back past a newer failed/unconfirmed authoritative source context.

M6-A delivered the shared Worker/source-composition `WarApiMapContextResolver` and M5-F now uses it.

M6-E and M6-G MUST reuse this resolver rather than create a second War API map-context policy.

The helper remains source-specific and MUST NOT leak War API types into FoxData.Application or Core.

## 8. Normalization semantics

M6 normalization is intentionally loss-minimizing.

The normalizer:

- consumes SourceParseRunId, never a transient DTO alone;
- resolves exact durable representation bytes;
- replays the versioned parser locally;
- verifies parse outcome, structural fingerprint, unknown counts and decoded length;
- derives exact sourceMapName from endpoint provenance;
- preserves nullable/open values;
- persists exact source occurrence order only as provenance;
- creates the snapshot and all child occurrences atomically with its accepted normalization run.

Initial normalizer versions:

~~~text
warapi-static-map-normalizer@1
warapi-dynamic-map-normalizer@1
~~~

Normalization MUST NOT:

- infer objective identity;
- deduplicate equal source occurrences;
- convert unknown iconType to a known icon;
- clear unknown flag bits;
- infer team ownership from icon appearance;
- infer source event time from polling cadence;
- merge static and dynamic payloads into a synthetic simultaneous snapshot.

## 9. Static and dynamic streams remain independent

Static and dynamic endpoints are independently polled snapshots.

M6 MUST NOT produce a fictitious combined source snapshot such as:

~~~text
static@12:00:00 + dynamic@12:00:17 = exact map state at 12:00:17
~~~

They remain separate observations with separate:

- Fetch provenance;
- source version;
- source lastUpdated;
- quality decision;
- coverage.

M7 may use nearby accepted static evidence as a matching feature, but it must retain the temporal uncertainty.

Static representation changes are allowed and retained. Upstream warapi#77 is the reason M6 cannot treat static data as immutable.

## 10. sourceRegionId enrichment

M5 intentionally leaves `WarRegion.sourceRegionId` nullable because active-map-list does not provide it.

M6 may enrich this projection, but only after an accepted map observation.

Rules:

1. normalization always preserves the raw map-level regionId on the snapshot;
2. quality evaluates whether regionId is representable and consistent;
3. if an accepted observation has non-null valid regionId and WarRegion.sourceRegionId is null, M6 may set it once;
4. if WarRegion already has the same value, no mutation is needed;
5. a conflicting non-null value MUST NOT overwrite the existing projection;
6. conflict produces a quality finding and blocks automatic canonical acceptance under policy v1;
7. historical map snapshots are never rewritten.

The accepted-observation insert and first-time WarRegion sourceRegionId enrichment SHOULD be one transaction.

## 11. Time semantics

M6 uses the same separated clocks as M5.

### observedAt

For a body-bearing representation:

~~~text
observedAt = authoritative representation Fetch.retrievedAt
~~~

For a cross-war 304 continuity binding:

~~~text
observedAt = authoritative validation Fetch.retrievedAt
~~~

This means "these exact bytes were known current at this observation boundary".

It does not mean every item changed at that instant.

### sourceUpdatedAt

`lastUpdated` is retained as:

- raw sourceLastUpdatedMs;
- safe UTC sourceUpdatedAt when representable.

It is map-level metadata only.

It MUST NOT become an item-level sourceEventAt.

### sourceVersion

`version` is stored verbatim as source metadata.

Comparisons are scoped to:

~~~text
same source
same shard
same endpoint capability
same exact sourceMapName
same war context where applicable
~~~

Static and dynamic version numbers MUST NOT be compared to one another as one sequence.

A gap proves only that intermediate source versions existed, not how many externally observable semantic changes FoxData missed.

## 12. Versioned taxonomy

Taxonomy interprets source codes without changing the preserved raw values.

Initial taxonomy identity:

~~~text
warapi-map-taxonomy@1
~~~

The versioned profile SHOULD live as a machine-readable repository contract under:

~~~text
.work/contracts/internal/source/
    warapi-map-taxonomy.schema.json
    warapi-map-taxonomy@1.json
~~~

A taxonomy profile entry for an icon type may contain:

- raw iconType;
- stable interpretation key;
- family key useful to later M7 matching;
- display label;
- documentation status;
- introduced update when documented;
- retired/legacy status when documented.

Taxonomy keys are interpretation keys, not ObjectiveId values.

### Parser diagnostics are not taxonomy authority

The existing `warapi-parser@1` persists unknown-code diagnostics as part of versioned source-parse provenance.

Those diagnostics are not semantic taxonomy truth.

In particular, M6 MUST NOT infer that an icon/team/flag is semantically unknown merely because the parser version counted it as an unknown code. The selected taxonomy profile is the sole authority for semantic known/unknown interpretation at the taxonomy stage.

This allows taxonomy profiles to evolve independently while preserving historical parser truth and SourceParseRun reproducibility.

### Unknown icon types

Unknown values remain:

~~~text
rawIconType = 97
interpretation = unknown
~~~

unless the selected taxonomy profile explicitly defines them from acceptable evidence.

M6 MUST NOT guess that icon 97 is a particular gameplay structure merely because an upstream issue or screenshot suggests a meaning.

Upstream warapi#137 is a mandatory fixture proving that an icon beyond the official README list survives normalization and quality processing.

### Flags

Always retain the raw integer bitmask.

For `warapi-map-taxonomy@1`, documented public bits may be interpreted:

- 0x01 IsVictoryBase;
- 0x02 legacy IsHomeBase;
- 0x04 IsBuildSite;
- 0x10 IsScorched;
- 0x20 IsTownClaimed.

Compute:

~~~text
knownBits
unknownBits = rawFlags & ~documentedKnownMask
~~~

Unknown bits are preserved and do not by themselves reject a snapshot.

### Teams

Raw teamId is always retained.

Known current values include:

- NONE;
- WARDENS;
- COLONIALS.

An unknown teamId is an open value, not a parser failure.

### viewDirection

`viewDirection` is preserved as raw source data when present.

Until the upstream contract documents stable semantics, M6 does not attach gameplay meaning to the number.

### Taxonomy execution model

M6-D SHOULD implement taxonomy as a pure version-selected interpreter over normalized occurrences.

Do not persist a second full copy of every occurrence merely to materialize taxonomy labels unless measurements show a query requirement.

QualityRun persists the taxonomy version it used.

M7 persists the taxonomy version used by each identity/matching run.

Older taxonomy profiles remain executable for deterministic replay.

## 13. Quality model

Normalization answers:

> Can FoxData represent what the source sent?

Quality answers:

> Should this representation be trusted as canonical map state?

Initial quality policy identity:

~~~text
warapi-map-quality@1
~~~

A quality policy explicitly references a taxonomy version.

Policy/configuration changes require a new version rather than silent threshold changes.

### Decisions

`accepted`:

- may create runtime.map_observations;
- may become a baseline for later quality evaluation;
- may be consumed by M7.

`suspect`:

- quality evidence is persisted;
- no automatic accepted runtime observation is created under policy v1;
- it is not a baseline;
- it remains locally reprocessable under a newer policy.

`quarantined`:

- explicitly blocking anomaly;
- no accepted runtime observation;
- never destroys raw/source/normalized evidence;
- not a baseline.

M6 does not partially delete individual items from a representation. Item-scoped findings may cause a whole-observation decision, but all source occurrences remain preserved.

## 14. Baseline selection

State-comparison quality rules require a deterministic baseline.

For dynamic map quality v1, baseline is:

~~~text
latest prior accepted runtime.map_observation
WHERE
    same WarRegion
    AND capabilityKind = dynamic
    AND observedAt < candidateObservedAt
ORDER BY observedAt DESC, durable tie-breaker
~~~

Suspect or quarantined observations are never baseline candidates.

Coverage gaps do not imply unchanged state.

A new war has no inherited dynamic baseline unless M6-G proves an explicit same-representation 304 continuity binding into that WarRegion.

Static and dynamic baselines are separate.

Out-of-order replay MUST select the same baseline as chronological replay or fail closed until prerequisites exist.

### Chronology barrier

For one `WarRegion + capabilityKind + taxonomyVersion + qualityPolicyVersion`, candidate C MUST NOT be quality-evaluated while any earlier authoritative source candidate has unfinished parse, normalization or quality work required by the selected versions.

The barrier starts from durable authoritative `captured_current` source evidence, not only from already-created MapSnapshot rows. Therefore an earlier body-bearing Fetch with a missing SourceParseRun, a SourceParseRun with a missing current M6 normalization, or a MapSnapshot with no terminal QualityRun all block a later candidate.

Ordering is:

~~~text
source observation boundary ASC
then deterministic durable tie-breaker
~~~

`captured_late` or superseded evidence does not block the canonical quality stream.

A `suspect` or `quarantined` result is terminal for ordering purposes but never becomes an accepted baseline.

This barrier prevents crash recovery of older durable source work from retroactively changing the baseline that a newer immutable QualityRun should have used.

M6-G reprocessing MUST use the same ordering rule.

M6-E/F tests must include chronological, out-of-order and concurrent processing and prove the same terminal baseline chain.

## 15. Required quality rule families

The exact thresholds are versioned configuration, not prose constants.

M6 MUST implement the following initial rule families.

### 15.1 region-id validity/conflict

Signals:

- negative/invalid regionId;
- accepted WarRegion sourceRegionId conflict;
- static and dynamic accepted observations disagreeing on regionId.

A conflicting non-null regionId blocks automatic acceptance in policy v1.

### 15.2 coordinate validity

Signals:

- x/y missing where an item type requires location;
- finite coordinates outside documented normalized bounds.

No coordinate rounding is used to "repair" source data.

M6 does not use coordinate equality as identity.

### 15.3 source version progression

Signals:

- version regression;
- unusually large version gaps;
- same version with changed exact representation;
- changed version with identical semantic content when measurable.

A version anomaly is evidence/quality information, not a fabricated count of missed events.

### 15.4 source lastUpdated progression

Signals:

- invalid epoch-millisecond value;
- regression relative to prior accepted observation;
- implausible mismatch with representation sequence.

It remains map-level metadata.

### 15.5 near-empty representation

Compare item/text counts with the deterministic accepted baseline.

A severe collapse is a quality signal.

The threshold must be calibrated and persisted in the new immutable `warapi-map-quality@2`; structural-only `@1` remains unchanged.

### 15.6 mass disappearance

Compare aggregate source-occurrence counts to the prior accepted baseline without attempting objective identity.

This is a representation-level anomaly only.

### 15.7 mass teamId=NONE / restart anomaly

This is the mandatory upstream warapi#92/#120 regression family.

The calibrated policy@2 rule MUST use multiple signals rather than `teamId == NONE` alone, because NONE can be legitimate.

Candidate features include:

- baseline has meaningful WARDENS/COLONIALS ownership;
- current NONE share rises sharply;
- owned-item share collapses sharply;
- total item count collapses;
- source version regresses/resets;
- representation composition collapses to a narrow subset of icon types.

Before freezing thresholds, M6-F MUST replay the candidate policy against:

1. the historical #92 golden fixture;
2. a healthy pre-incident fixture;
3. a healthy recovery fixture;
4. available M4 dynamic-map evidence;
5. war-start/no-baseline fixtures where neutral state may be legitimate.

Acceptance gate:

- the historical #92 fixture is quarantined;
- the preceding healthy snapshot remains accepted;
- the healthy recovery snapshot is accepted;
- the quarantined snapshot never replaces the accepted baseline;
- unknown icon/team/flag values alone do not trigger the mass-NONE quarantine;
- no exact objective/change event is emitted because M6 does not own events.

### 15.8 structural/schema drift

Structural fingerprint change creates a finding and telemetry.

It is not automatic rejection when the versioned parser successfully preserves the representation.

### 15.9 unknown taxonomy values

Unknown icon/team/flag values create informational/suspect findings according to policy, but do not automatically quarantine the entire representation.

### 15.10 duplicate occurrence signal

Exact or near-exact duplicate occurrences may be recorded as a quality finding.

M6 MUST preserve both occurrences.

Upstream warapi#115 is the regression evidence.

The initial effect SHOULD be non-blocking unless additional evidence proves corruption.

## 16. Quality-policy calibration

M6-F MUST NOT freeze arbitrary anomaly thresholds directly in C#.

Create a versioned configuration artifact, for example:

~~~text
warapi-map-quality-policy@2.json
~~~

The implementation workflow is:

1. define candidate features and rule keys;
2. replay them offline over historical/golden fixtures and available M4 evidence;
3. inspect false-positive candidates;
4. publish calibrated anomaly policy@2 as immutable versioned repository data, retaining the already-used structural-only policy@1 unchanged;
5. record the full selected quality-policy version on every QualityRun and each rule configuration version on its findings;
6. regression-test both frozen structural-only @1 and calibrated @2 behavior;
7. change any rules or thresholds only by publishing another new policy version.

A dedicated heavy external data-quality framework is not required for M6. The required logic is source-specific, deterministic, small enough to keep under FoxData's own versioned contracts, and must integrate directly with durable evidence provenance.

## 17. Coverage extension

M5-G coverage currently closes war/maps/report capability gaps.

M6-G extends the same model to:

- `static-map-state`;
- `dynamic-map-state`.

Coverage remains:

- observed;
- source_not_modified;
- source_unavailable;
- collector_unavailable;
- rejected;
- unknown.

For static/dynamic 304:

- validation Fetch is retained;
- exact prior body-bearing representation Fetch is retained;
- no duplicate Payload;
- no duplicate SourceParseRun;
- no duplicate normalized map snapshot.

Ordinary same-WarRegion 304 extends coverage only.

If the same exact map representation is validated after a WarRegion transition and no accepted observation exists for the new WarRegion, M6-G may create one versioned continuity evaluation/binding using the existing snapshot, provided M5 proves the new WarRegion at the validation boundary.

This is analogous to M5-G active-map-list continuity, but it MUST NOT duplicate source occurrences.

## 18. Coverage-store generalization

Do not expand M5-G by adding more War API string literals and SQL CASE branches every milestone.

M6-G SHOULD generalize canonical reprocessing selection around a source-neutral capability plan supplied by the Worker composition layer.

Conceptually:

~~~text
CoverageCapabilityPlan
    capabilityKey
    parserVersion
    normalizerVersion
    reprocessor kind / dependency rank
~~~

Infrastructure should query durable candidates from supplied plan data rather than encode War API capability semantics.

Worker remains responsible for dispatching a War API capability to its source-specific normalizer.

G1 keeps the existing M5 capability set unchanged while replacing SQL capability literals and normalizer CASE branches with supplied plan data. G1 is complete in PR #51. G2 is the first slice allowed to add static-map-state and dynamic-map-state to coverage/reprocessing selection. G2/G3 additionally guarantees that repeated 304 validation of one durable representation reuses a single locally repaired parser result and does not duplicate Payload, SourceParseRun or normalized state.

This refactor must preserve M5-H behavior exactly.

## 19. Recovery and reprocessing

M6 local recovery must handle:

- raw static/dynamic Fetch committed but SourceParseRun missing;
- SourceParseRun committed but M6 normalization missing;
- normalized snapshot committed but QualityRun missing;
- accepted QualityRun committed atomically with runtime observation;
- deferred map context waiting for M5 war/region evidence;
- 304 continuity waiting for map snapshot normalization;
- policy/taxonomy upgrade reprocessing over existing snapshots.

No recovery step requires a new upstream request for already durable work.

Dependency order:

~~~text
M6 static/dynamic source parse repair
    -> M6 source-local map normalization
        -> normalized map snapshot

M5 war context + M5 WarRegion
    + normalized map snapshot
        -> taxonomy selection
            -> quality evaluation
                -> accepted map observation
~~~

Source parsing/normalization and M5 context recovery may progress independently until the quality/binding join point. G4 implements this by routing static/dynamic SourceParseRun normalization gaps through the same source-neutral recovery candidate query used by M5 canonical recovery, while keeping map normalization entirely source-local.

G5 closes the body-bearing 200 normalization-to-quality crash gap with a version-aware scanner over the selected static/dynamic parser and normalizer identities. The scanner uses validation-boundary identity and routes every repair through the normal M6-E quality coordinator/store so chronology and baseline selection are rechecked under the existing PostgreSQL WarRegion lock. Deferred prerequisites remain retryable; same-version late history blocked by an already-terminal later quality result remains explicitly version-blocked for G8 rather than being treated as completion.

G6 extends that scanner to exact 304 -> representation lineage without creating new parsing or normalized source state. Exact lineage also requires the 304 validation boundary to be strictly later than its reused body-bearing representation under the same `retrieved_at + FetchId` total order used by quality chronology; non-forward provenance fails closed in both scanning and ordering. Before a 304 enters quality evaluation, the Worker resolves map context at both the reused representation boundary and the 304 validation boundary through the existing M5 context resolver. The ordering barrier includes earlier authoritative 304 validation boundaries, so a missing earlier 304 QualityRun cannot be skipped when establishing a later baseline. Eligible same-war 304 binds the existing MapSnapshot to the new validation Fetch through the unchanged M6-E quality transaction.

G7 permits those two proven contexts to belong to different wars. The validation-side WarRegion is the authoritative target of the new quality identity, while the snapshot and source occurrences remain the immutable representation created from the original body-bearing Fetch. M5 war/map-list continuity must already prove membership at the validation boundary; otherwise recovery remains deferred. Chronology and baseline lookup remain scoped to the target WarRegion, so the first accepted observation in a new war starts with no baseline from the previous war even when both observations reuse the same MapSnapshot. Subsequent validations in the new WarRegion may baseline only on accepted observations from that WarRegion. A same-war resolution that somehow yields two different WarRegion identities for the same source map is an integrity failure, not cross-war continuity. G7 does not activate policy@2.

Quality/taxonomy reprocessing MUST NOT rewrite old runs. New versions create new immutable derived results.

## 20. Concurrency and transaction rules

### Snapshot normalization

One transaction commits:

~~~text
normalization_run
+ map_snapshot
+ all map_item_occurrences
+ all map_text_occurrences
~~~

A successful map normalization run cannot exist without its complete child set.

Replay returns the same snapshot.

### Quality acceptance

One transaction commits:

~~~text
quality_run
+ quality_findings
+ accepted runtime.map_observation when decision=accepted
+ optional first-time WarRegion.sourceRegionId enrichment
~~~

For suspect/quarantined decisions, the same transaction commits only quality evidence.

A successful accepted QualityRun cannot exist without its runtime map observation.

### Baseline race

Quality evaluation that depends on a prior accepted baseline must prevent two concurrent candidates for the same WarRegion/capability from silently selecting inconsistent baselines.

M6-E must choose and test one PostgreSQL-enforced serialization strategy.

The current execution plan selects `SELECT ... FOR UPDATE` on the target `runtime.war_regions` row as the v1 serialization boundary. This deliberately serializes static and dynamic quality acceptance for one WarRegion until measurements justify a finer scope.

Do not rely on in-process locks because Worker process death and future horizontal workers must remain safe.

M6-E must prove the row-lock strategy under concurrent integration tests and record measured contention. If measurements show unacceptable serialization, a later version may move to transaction-level advisory locking or SERIALIZABLE retry, but that change must preserve the same chronology/baseline semantics. An ADR is required only if this introduces a new cross-system infrastructure guarantee.

## 21. Observability

Add low-cardinality metrics for:

- map normalization runs by capability/outcome;
- source occurrence counts;
- taxonomy unknown icon count;
- taxonomy unknown flag-bit observations;
- quality runs by decision;
- quality rule hits by rule key/version;
- quarantine count by rule family;
- M6 recovery repairs/deferred work;
- static/dynamic coverage states;
- continuity bindings.

Do not use mapName, WarRegionId, item coordinates, raw icon codes or evidence IDs as broad metric labels.

Those belong in structured logs/traces.

## 22. Performance verification

M6-H must measure at least:

- normalized snapshots/day;
- item occurrences/snapshot p50/p95/p99/max;
- text occurrences/snapshot;
- rows/bytes written per changed static/dynamic representation;
- quality evaluation duration;
- baseline lookup duration;
- deterministic rebuild throughput;
- unknown icon/flag frequency;
- quarantine/suspect rate.

M6 must not infer volume from poll count because 304 validation should not duplicate snapshots.

The M4 three-region probe observed 26 dynamic representation episodes over eight hours at the high-resolution cohort; M6 may use this as historical planning evidence, not as a universal future-war rate.

## 23. Testing strategy

### Unit tests

Cover pure logic:

- exact source-map semantic-key parsing;
- timestamp conversion;
- taxonomy v1 known/unknown icons;
- documented and unknown flag bits;
- team open-value handling;
- quality feature extraction;
- rule thresholds/config parsing;
- decision aggregation;
- canonical ordering/tie-breakers.

### Source tests

Cover War API DTO/parser behavior:

- static fixture;
- dynamic fixture;
- unknown icon 97;
- additive viewDirection;
- unknown team;
- unknown flag bits;
- duplicate occurrence;
- missing nullable fields;
- empty arrays versus null/missing arrays;
- malformed shapes.

### Integration tests

Use PostgreSQL/Testcontainers.

Cover:

- snapshot + occurrence atomicity;
- idempotent normalization;
- exact provenance;
- static/dynamic source normalization without WarRegion context;
- quality-time static/dynamic context resolution;
- sourceRegionId enrichment and conflict;
- quality-run atomic acceptance;
- suspect/quarantine no-runtime-observation behavior;
- out-of-order baseline selection;
- static changes retained;
- dynamic version regression finding;
- cross-war 304 snapshot reuse without duplicate occurrence rows;
- coverage states for static/dynamic;
- local recovery without new Fetch.

### Historical golden fixtures

Mandatory fixtures:

- upstream warapi#92 restart/mass-NONE representation;
- pre-incident healthy baseline;
- post-restart healthy recovery;
- warapi#137 iconType 97 + viewDirection;
- warapi#115 duplicate Rocket Target occurrence;
- static representation change regression inspired by warapi#77;
- HomeRegion capability-asymmetry behavior.

### Recovery tests

Independently prove process-death windows:

- after raw capture/before parse;
- after parse/before map normalization;
- after snapshot transaction/before quality;
- after deferred quality dependency;
- after 304 coverage/before continuity binding.

### Contract/dependency tests

M6 must preserve:

- Core has no source/Infrastructure dependency;
- Application has no Infrastructure/WarApi dependency;
- source-specific taxonomy/profile loading stays outside Core;
- API/Chronicle does not enter M6 canonical contracts.

## 24. Slice plan

### M6-A — Contract, context and persistence foundation

Status: complete.

Goal: freeze the M6 semantic boundary before map normalization.

Delivered:

- UUIDv7 typed identities for normalized map snapshots, item/text occurrences, quality runs/findings and accepted map observations;
- source-neutral Application contracts plus MapSnapshotKernel;
- PostgreSQL M6 foundation schema for representation-derived snapshots/occurrences, quality evidence and accepted map observations;
- explicit `quality` schema creation;
- exact per-array ordinal uniqueness without value-based deduplication;
- typed item/text occurrence references from quality findings;
- composite database lineage preventing a runtime map observation from mixing the snapshot, WarRegion or validation Fetch of another QualityRun;
- reusable `WarApiMapContextResolver` extracted from M5-F and adopted by war-report normalization;
- migration round-trip, duplicate-preservation, snapshot-reuse, fail-closed constraint and architecture-boundary tests;
- no static/dynamic map normalization, taxonomy interpretation or quality-policy behavior yet.

The implementation deliberately permits representable anomalous raw values through MapSnapshotKernel. Coordinate validity, negative/changed source region values, unknown teams/icons/flags and similar semantic quality questions remain M6-E/F policy work.

Original implementation plan:

1. this normative M6 specification and research snapshot;
2. typed IDs/contracts for map snapshots, source occurrences, quality runs/findings and accepted map observations;
3. persistence schema/migration for:
   - map snapshots;
   - item occurrences;
   - text occurrences;
   - quality runs;
   - quality findings;
   - runtime map observations;
4. database uniqueness/FK/check constraints;
5. source-neutral Application kernels/interfaces;
6. shared Worker-level War API map-context resolver extracted from existing M5-F semantics;
7. schema/dependency tests;
8. no live behavior change yet.

Acceptance:

- migration round-trip green;
- M5-H remains green unchanged;
- no stable objective identity introduced;
- duplicate source occurrences are representable;
- 304 representation reuse is representable without copying occurrences;
- source-specific types do not leak into Core/Application.

### M6-B — Static map normalization

Status: complete.

Goal: deterministically materialize body-bearing static representations as normalized source snapshots without requiring canonical WarRegion context.

Delivered:

1. `warapi-static-map-normalizer@1`;
2. exact SourceParseRunId-based replay from durable body-bearing evidence;
3. fail-closed verification of source/endpoint capability, semantic key, adapter/parser/fingerprint identity, parse outcome, structural fingerprint, unknown counters and decoded length;
4. exact case-sensitive sourceMapName extraction from `map-static/<name>`;
5. source-local normalization independent from M5 War/WarRegion availability;
6. atomic PostgreSQL transaction for:
   - normalization_run;
   - map_snapshot;
   - all map item occurrences;
   - all map text occurrences;
7. set-based PostgreSQL occurrence insertion using `unnest`, avoiding one round-trip per source occurrence;
8. exact source occurrence multiplicity and source-array ordinal preservation without value-based deduplication;
9. raw preservation of sourceRegionId, scorchedVictoryTowns, source version, raw lastUpdated, teamId, iconType, x/y, flags, viewDirection and text-marker fields;
10. safe conversion of source lastUpdated to UTC only when representable; unrepresentable raw milliseconds remain durable while sourceUpdatedAt is null;
11. durable empty-array versus null/missing-array metadata and database constraints;
12. terminal rejected normalization for unsuccessful source parses with no fabricated map snapshot;
13. live Worker wiring immediately after the durable static SourceParseRun is recorded;
14. idempotent replay returning the existing normalization/snapshot/occurrence graph;
15. no extra source request, no taxonomy interpretation, no quality decision and no ObjectiveId.

The generic `PostgresMapSnapshotStore` introduced here is intentionally reusable by M6-C dynamic normalization. It revalidates exact source-parse provenance inside the same transaction that commits normalized content.

Implementation verification head `3a2a83b7b0e8a4d27ab04b741cf2d209e1e3615d` passed:

- Release build and CLI bootstrap;
- 28 unit tests;
- 94 integration tests;
- 15 recovery tests;
- 114 source tests;
- 4 contract tests;
- 255 tests total, 0 failed, 0 skipped;
- Docker Compose migration/API readiness/OpenAPI/Worker smoke;
- Contracts workflow;
- Dependency Review.

Acceptance:

- identical replay returns the same snapshot/children — verified;
- static representation changes remain separate snapshots — supported by representation identity and append-only normalization;
- duplicate items remain duplicated — verified;
- unknown/open values survive normalization — verified;
- no item/objective identity is inferred — verified by contract boundary;
- static normalization does not require WarRegion context — verified;
- empty arrays remain distinguishable from null/missing arrays — verified;
- malformed/unsuccessful source parse creates terminal rejection but no snapshot — verified;
- 304 does not create a duplicate parse or normalized snapshot because normalization is invoked only for a new body-bearing SourceParseRun — preserved.

### M6-C — Dynamic map normalization

Status: complete.

Goal: materialize dynamic/public representations with the same loss-minimizing, source-local model as static normalization, while keeping static and dynamic streams semantically independent.

Delivered:

1. `warapi-dynamic-map-normalizer@1`;
2. one shared Worker-level durable map-normalization core used by both static and dynamic wrappers, preventing replay/provenance semantics from drifting between the two source capabilities;
3. exact SourceParseRunId-based replay from body-bearing durable evidence;
4. fail-closed source/endpoint capability, semantic-key, adapter/parser/fingerprint and parser-replay verification;
5. exact case-sensitive sourceMapName extraction from `map-dynamic/<name>`;
6. source-local normalization without requiring WarId or WarRegion context;
7. atomic reuse of the M6-B `normalization_run + map_snapshot + occurrence rows` transaction;
8. exact source item/text multiplicity, ordinal and array-presence preservation;
9. raw preservation of teamId, iconType, flags, viewDirection, coordinates, map-marker values, regionId, scorchedVictoryTowns, source version and lastUpdated;
10. safe UTC conversion of lastUpdated only when representable while retaining the raw milliseconds independently;
11. live Worker normalization after durable dynamic SourceParseRun creation;
12. terminal normalization rejection for unsuccessful source parses without fabricating a snapshot;
13. idempotent replay returning the existing dynamic snapshot;
14. 304 behavior that reuses the existing representation and creates no new SourceParseRun or map snapshot;
15. no taxonomy, quality acceptance, objective identity or static/dynamic synthetic merge.

Acceptance verified by integration coverage:

- iconType 97 and unknown team/flag values normalize rather than disappear;
- parser `parsed_with_unknowns` diagnostic does not block loss-minimizing normalization;
- viewDirection is preserved without invented semantics;
- exact duplicate source occurrences remain distinct by representation-local ordinal;
- unrepresentable lastUpdated retains its raw value with nullable converted UTC time;
- dynamic representation revision produces a new append-only snapshot;
- 304 validation produces no duplicate source parse, normalization or snapshot;
- malformed dynamic source JSON records rejected normalization with no snapshot;
- identical numeric source version values can coexist in static and dynamic snapshots without comparison or uniqueness coupling;
- normalization succeeds without WarRegion context.

M6-D is the first slice allowed to assign versioned semantic meaning to raw icon/team/flag values.

### M6-D — Versioned taxonomy

Status: complete.

Goal: provide deterministic semantic interpretation without mutating normalized source occurrences or converting parser diagnostics into semantic truth.

Delivered:

1. executable JSON Schema contract `warapi-map-taxonomy.schema.json`;
2. immutable profile `warapi-map-taxonomy@1.json`;
3. one build/runtime source of truth: the normative `.work` profile is embedded directly into `FoxData.Sources.WarApi` rather than copied into C# constants;
4. source-generated System.Text.Json metadata for AOT-friendly profile loading;
5. fail-closed version registry: an unknown taxonomy version is not silently upgraded to the newest profile;
6. pure `WarApiMapTaxonomyInterpreter` with no database, Worker or wall-clock dependency;
7. all icon codes currently documented by the Official War API README as of the profile review date, with stable `icon.*` keys and documented removal/update metadata where available;
8. exact current documented team values with stable `team.*` keys;
9. all documented public flag bits with stable `flag.*` keys;
10. raw signed flag preservation plus unsigned bit-pattern decomposition into known and unknown bits;
11. exact/open team and icon behavior: unknown raw values remain unknown and are never coerced to a nearby known entry;
12. removed/legacy taxonomy entries remain interpretable and do not erase historical raw source meaning;
13. Contracts CI validation of the profile against JSON Schema Draft 2020-12;
14. source/golden tests for documented entries, removed metadata, icon 97, unknown teams, unknown flag bits and negative/raw flag bit patterns;
15. no persistence duplication of taxonomy labels or interpreted occurrence rows.

The v1 profile intentionally does not define an objective-family hierarchy. M7 may introduce versioned matching families later if its evidence requires them. M6-D only assigns meanings directly supported by the selected taxonomy contract.

The existing `warapi-parser@1` `unknownCodeCount` remains a parser-time provenance diagnostic. It is not taxonomy authority. For example, iconType 97 currently contributes to parser unknown-code diagnostics and remains `unknown` under `warapi-map-taxonomy@1`; a future taxonomy version may interpret a code without rewriting the historical parser run.

Flag interpretation is deliberately lossless:

~~~text
raw bit pattern
    -> knownBits according to taxonomy version
    -> unknownBits preserved exactly
    -> list of known flag entries
~~~

A zero bitmask is fully known with no active flags. A null flag field is missing rather than equivalent to zero.

Acceptance:

- current documented codes produce stable versioned interpretation keys — verified;
- icon 97 remains raw and unknown in taxonomy@1 — verified;
- parser `unknownCodeCount` and taxonomy known/unknown status remain separate contracts — verified;
- unknown teams remain exact/open — verified;
- unknown flag bits round-trip without invented semantics — verified;
- removed flag/icon entries remain interpretable with lifecycle metadata — verified;
- changing taxonomy meaning requires a new profile/version — enforced by explicit registry identity;
- schema/profile validation is part of the Contracts workflow — implemented.

M6-E MUST record the exact taxonomy version used by every QualityRun. It consumes taxonomy interpretation but does not mutate normalized occurrence rows.
### M6-E — Quality kernel and accepted-observation transaction

Progress: E1 and E2 merged (#45/#47); E3 chronology, source-neutral ordering reader, War API coordinator, Worker and local normalized-snapshot repair under implementation in #48. E3 acceptance pending verification.

Goal: introduce versioned quality decisions without anomaly heuristics being hidden inside normalizers.

Implement:

1. quality policy schema/profile infrastructure;
2. `warapi-map-quality@1` initial structural policy shell;
3. QualityRun / finding persistence;
4. M5-backed validation-boundary context resolution through `WarApiMapContextResolver`;
5. deterministic accepted baseline resolver;
6. PostgreSQL concurrency protection for baseline selection;
7. chronology-barrier query preventing evaluation past unresolved earlier candidates;
8. decision aggregation;
9. atomic accepted QualityRun + runtime.map_observation;
10. first-time sourceRegionId enrichment after acceptance;
11. suspect/quarantine persistence without runtime observation;
12. recovery of normalization-complete / quality-missing work.

Initial non-calibrated rule support:

- regionId validity/conflict;
- coordinate validity;
- timestamp representability;
- structural fingerprint change;
- unknown taxonomy diagnostics.

Acceptance:

- quality logic is versioned separately from normalization;
- a quarantine never deletes evidence;
- accepted observation and quality run cannot diverge after crash;
- baseline selection is deterministic under concurrent/out-of-order processing.

### M6-F1 implementation checkpoint

The F1 evidence/feature layer is complete in PR #49. It deliberately stops before
policy calibration. The fixture catalog distinguishes exact upstream bytes from
synthetic/minimized regressions, and the offline feature lab records exact payload
SHA-256 plus parser structural fingerprints so later calibration can be audited.

F1 does not assign anomaly decisions. M6-F2 owns calibration, false-positive review
against retained M4 evidence and publication of a new immutable policy@2.

Calibration evidence, thresholds, limitations and source artifact digests are recorded in
[M6_F2_CALIBRATION.md](M6_F2_CALIBRATION.md). Policy@2 is executable and contract-validated, but runtime selection remains policy@1 until M6-G performs ordered versioned reprocessing.

### M6-F — Historical anomaly rules and calibrated policy v2

Goal: freeze the first operational anomaly map-quality policy without mutating structural policy@1. Runtime activation is a separate M6-G reprocessing step.

Implement:

1. offline feature extraction over fixtures/M4 evidence;
2. map version regression/gap rules;
3. lastUpdated regression rule;
4. near-empty rule;
5. mass disappearance rule;
6. mass teamId=NONE/restart composite rule;
7. duplicate occurrence signal;
8. exact policy thresholds/configuration in versioned JSON;
9. historical #92/#120 regression fixtures;
10. telemetry for rule hits.

Acceptance:

- #92 fixture quarantined;
- healthy baseline/recovery accepted;
- quarantine cannot replace accepted baseline;
- legitimate unknown icon/team/flag alone cannot trigger mass-NONE quarantine;
- thresholds exist only in versioned policy/config data, not scattered constants.

### M6-G — Coverage, recovery and reprocessing

Goal: make static/dynamic M6 as crash-recoverable as M5.

Implement:

1. extend coverage classification to static/dynamic;
2. generalize capability→normalizer reprocessing plan instead of hardcoded M5 SQL CASE growth;
3. local source-parse repair for static/dynamic representations;
4. normalization gap scanner;
5. quality gap scanner;
6. same-war 304 coverage without duplicate snapshots;
7. cross-war 304 snapshot binding/quality evaluation without duplicate occurrences;
8. taxonomy/quality version reprocessing;
9. fail-closed deferred work reporting;
10. independent RecoveryTests coverage.

Acceptance:

- recovery creates no new upstream Fetch for durable work;
- 304 creates no duplicate Payload/SourceParseRun/map snapshot;
- cross-war validated representation can be bound to the new WarRegion only through proven M5 context;
- M5-H remains green after coverage-store generalization.

### M6-H — Completion gate

Goal: close M6 only when map normalization/quality is reconstructable and provenance-complete.

Representative graph must include:

- one changing static representation;
- multiple dynamic representations;
- healthy baseline;
- issue-92 quarantine representation;
- healthy recovery representation;
- unknown icon/flag/team;
- duplicate occurrence;
- 304 validation;
- cross-war 304 continuity;
- source/collector/rejected/unknown coverage cases.

Completion procedure:

1. converge M5+M6 local recovery;
2. require zero deferred/outstanding M6 work in the fixture;
3. audit snapshot/occurrence/quality/runtime provenance;
4. audit accepted-observation baseline consistency;
5. capture semantic M6 snapshot;
6. delete only M6-derived rows while preserving M2/M3/M5 authoritative evidence/context;
7. rebuild without HTTP;
8. repeat audits;
9. require semantic equality;
10. publish `M6_COMPLETION.md` with exact CI/test evidence.

Semantic equality includes:

- source snapshot values;
- exact source occurrence multiplicity/order-as-provenance;
- taxonomy/policy versions;
- quality decisions/findings;
- accepted observation boundaries;
- durable source/evidence IDs.

Generated M6 surrogate IDs and local processing timestamps are not public semantic identity.

## 25. Recommended implementation order

Do not parallelize the semantic foundation slices.

Required order:

~~~text
M6-A
  -> M6-B
  -> M6-C
  -> M6-D
  -> M6-E
  -> M6-F
  -> M6-G
  -> M6-H
~~~

Rationale:

- quality cannot be designed correctly without normalized source occurrences;
- anomaly rules should not be embedded in normalization;
- taxonomy must exist before quality rules rely on icon families;
- recovery should be generalized after the normalizer/quality work identities are stable;
- completion comes only after full offline rebuild is possible.

Implementation PRs SHOULD stay slice-scoped.

Each slice PR must:

1. read this specification and affected authority docs;
2. update the spec if an invariant changes;
3. keep unknown/open source values lossless;
4. add executable tests before claiming completion;
5. pass format/build/unit/integration/recovery/source/contract tests and Docker smoke where schema/runtime changes are involved;
6. avoid unrelated M7/M8/API work.

## 26. Decisions deliberately deferred to M7

M6 output is designed to be sufficient input for M7 without deciding identity.

M7 receives:

- accepted map observations;
- exact source occurrences;
- normalized coordinates;
- raw and versioned taxonomy interpretation;
- static evidence;
- map text evidence;
- quality decision/finding references;
- observation/coverage boundaries.

M7 then owns:

- within-war ObjectiveInstance;
- candidate generation;
- distance/features;
- icon-family compatibility;
- score/margin;
- ambiguous/unmatched decisions;
- append-only manual override model.

M6 MUST NOT make M7 easier by secretly creating pseudo-objective IDs from coordinate/icon hashes.

## 27. Completion definition

M6 is complete only when FoxData can answer all of the following from durable local evidence:

- What exact static/dynamic representation did the source return?
- Which exact source occurrences were in it, including duplicates and unknown values?
- Which taxonomy version interpreted those values?
- Which quality rules ran, with what inputs/configuration and decision?
- Was the representation accepted, suspect or quarantined?
- To which WarRegion and validation boundary was an accepted observation bound?
- Was that binding based on a body-bearing Fetch or proven 304 continuity?
- Can all M6-derived state be deleted and deterministically rebuilt without a new upstream request?
- Can the issue-92 historical false-state fixture be prevented from becoming accepted canonical map state?

If any answer depends on current wall-clock state, undocumented source guarantees, lost configuration, array-index identity or a new HTTP fetch, M6 is not complete.
