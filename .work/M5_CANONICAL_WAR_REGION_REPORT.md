# M5 — Canonical War / Region / Report Model

Status: complete. M5-A through M5-H implemented; successor M6.
Prerequisite: M4 Source Measurement completed.
Successor: M6 maps, taxonomy and quality.

M5 is the first canonical-state milestone. It consumes durable M2/M3 evidence under the M4 collection-policy model and introduces consumer-neutral canonical war, region and war-report state without changing source collection semantics.

## Scope

M5 owns:

- canonical war identity and immutable war observations;
- canonical region identity and within-war source-region membership;
- immutable war-report observations;
- versioned normalization-run provenance;
- explicit observed-time semantics;
- coverage representation for war/region/report collection;
- local replay/reprocessing from durable evidence.

M5 does not own:

- static/dynamic map item normalization;
- icon/flag taxonomy;
- issue-92 mass-NONE anomaly policy or other map-quality rules;
- objective identity;
- state intervals/change events;
- Chronicle-specific concepts or UI projections.

Those remain M6+ responsibilities.

## Pipeline

```text
M2 immutable Fetch/Payload
        |
M3 SourceParseRun
        |
M5 NormalizationRun
        |
minimal M5 acceptance
        |
canonical immutable observation
        |
consumer projections
```

A canonical observation MUST be reproducible from durable evidence. M5 MUST NOT require a new upstream request to recover work lost after source parsing.

## Identity

### War

The source identity is conceptually:

```text
(source, environment, shard, sourceWarId)
```

The physical schema may use `shardId + sourceWarId` because a registered shard already fixes source and environment.

`warNumber` is source metadata. It is shard-scoped and MUST NOT be used as the primary identity.

`sourceWarId` is opaque. It MUST NOT be parsed for meaning.

### Region

`sourceMapName` is an opaque, case-sensitive source identifier and is retained verbatim on the within-war membership.

M5 MUST NOT:

- strip or append `Hex`;
- case-fold map names;
- infer API identifiers from asset names;
- merge source names because they look similar.

The initial canonical region key may equal an exact source map name, but that does not establish cross-source alias equivalence. Any later alias/taxonomy policy is versioned work owned by M6+.

### WarRegion

A WarRegion binds one canonical region to one war and retains the exact source map name observed for that membership.

`sourceRegionId` is optional because the active map list does not provide it. Later map observations may enrich it without rewriting historical observations.

## Time semantics

M5 preserves the distinctions in TIME_SEMANTICS.md:

- `retrievedAt`: source retrieval completed;
- `observedAt`: accepted canonical observation boundary;
- `sourceUpdatedAt`: only when supplied by the source;
- `recordedAt`: canonical row durably recorded;
- source lifecycle timestamps: source-provided timestamps normalized to UTC.

For a body-bearing representation, the initial M5 `observedAt` is derived deterministically from its authoritative Fetch retrieval boundary unless a more specific documented source event time exists.

M5 MUST NOT fabricate event timestamps between polls.

`dayOfWar` remains source data and is not the canonical global war clock.

## Normalization runs

Every canonical observation MUST reference a durable `evidence.normalization_runs` row.

A normalization run records at minimum:

- sourceParseRunId;
- normalizerVersion;
- outcome;
- optional errorCode;
- startedAt;
- completedAt;
- createdAt.

Idempotency key:

```text
(sourceParseRunId, normalizerVersion)
```

Re-running the same normalizer over the same parse run MUST return/recognize the existing equivalent result rather than create divergent canonical history.

A new normalizer version creates a new derived run over the same immutable evidence.

## Minimal M5 acceptance gate

M5 acceptance is intentionally narrow. It may reject normalization when:

- the source parse did not succeed;
- required identity material is absent or invalid;
- provenance is incomplete;
- a timestamp cannot be represented safely;
- a numeric field violates a structural domain bound such as a negative casualty count.

M5 MUST preserve unknown/open source values where they are structurally representable. Unknown winner/team values are not, by themselves, a reason to discard raw evidence.

Map anomaly policies, taxonomy confidence and mass-state anomaly detection are M6 work and MUST NOT be smuggled into M5.

## Canonical persistence

### evidence.normalization_runs

Append-only derived provenance.

### runtime.wars

Stable identity/projection row. It may update bounded discovery metadata such as first/last observed boundaries, but it is not historical truth by itself.

Required uniqueness:

```text
(shardId, sourceWarId)
```

### runtime.regions

Stable canonical region identity.

### runtime.war_regions

Within-war region membership retaining exact source naming.

Required uniqueness:

```text
(warId, sourceMapName)
```

### runtime.war_observations

Append-only accepted war snapshots. They contain the normalized source war fields and reference:

- warId;
- normalizationRunId;
- representationFetchId;
- observedAt;
- recordedAt.

A current-war view is a projection over observations; M5 MUST NOT implement current state by overwriting the prior observation.

### runtime.war_report_observations

Append-only accepted map war-report snapshots with the same evidence/provenance rules.

## Coverage

Coverage is independent from domain state.

M5 coverage states are:

- `observed`;
- `source_not_modified`;
- `source_unavailable`;
- `collector_unavailable`;
- `rejected`;
- `unknown`.

A 304 can extend trustworthy observation coverage without creating a duplicate semantic observation.

A 503, collector outage or other gap MUST NOT be interpreted as "state did not change".

The persistence representation for coverage is delivered in the later M5 coverage slice after the canonical identity/observation foundation is stable.

## M5-D transaction and replay boundary

Accepted war normalization uses one PostgreSQL transaction for:

~~~text
normalization_run
    + war identity/projection
    + immutable war_observation
~~~

A successful normalization run MUST NOT become durable without its matching canonical observation.

The normalizer receives a SourceParseRunId, not an in-memory source DTO. It resolves the exact representation Fetch/Payload from durable evidence and reruns the parser version recorded by the source parse run. This keeps live reconciliation and post-crash replay on the same evidence path.

runtime.wars is a bounded projection only. Its observation bounds and projected warNumber are rebuilt from runtime.war_observations and MUST NOT be treated as historical truth.

## M5-E region-membership boundary

An active-map-list payload has no `warId`. M5 therefore MUST NOT attach it to whichever war happens to be current when replay executes.

For a maps parse with canonical observation boundary `mapsObservedAt`, M5-E first resolves the latest authoritative immutable War API fetch for the same shard and the `runtime-war-state / war` endpoint with:

~~~text
attempt.outcomeCode = captured_current
AND warFetch.retrievedAt <= mapsObservedAt
ORDER BY warFetch.retrievedAt DESC
~~~

A late/fenced-out capture is evidence but is not authoritative chronology and MUST NOT select the war context.

The selected war fetch must confirm a replayable representation: either a body-bearing successful representation or a 304 linked to its prior body-bearing Fetch, with the matching versioned source parse run. M5-E does not skip a newer failed or otherwise unconfirmed authoritative war fetch to assume that an older representation remained current; such a gap is deferred because source unavailability is not proof of unchanged state.

M5-E then invokes the versioned M5-D war normalizer for that exact source parse run. The resulting canonical `WarId` is used for membership, and the canonical store independently verifies the same temporal war context before commit.

If no qualifying/replayable war source context exists, region membership normalization is deferred. Deferred work creates no `normalization_runs` row and remains replayable from the same durable source parse after suitable durable war evidence or parse metadata appears.

Once `(sourceParseRunId, regionNormalizerVersion)` is durably normalized or rejected, that result is terminal for that version. Replay recognizes the existing run and MUST NOT resolve a new war context or mutate membership again. A new normalization policy requires a new normalizer version.

This rule makes local replay independent of canonical worker order and stable across later wars, and prevents historical map evidence from being silently rebound to a future or backfilled war.

`runtime.regions` is source-scoped by canonical key in M5. `runtime.war_regions` retains the exact upstream source map name and does not infer aliases, strip suffixes, case-fold, or treat absence from a later list as a deletion.

## M5-E and 304 validation

M5-E normalizes semantic active-map-list content from a body-bearing representation and its durable `SourceParseRun`.

A `304 Not Modified` validation does not create another body-bearing source parse and MUST NOT create a second normalization run for the same representation merely to extend time coverage. The `source_not_modified` continuity record is owned by M5-G coverage.

If unchanged map-list content is validated across a war boundary, M5-G MUST preserve that validation provenance and may use it to prove continuity/projection for the new war without violating the M5 normalization idempotency key or fabricating a new source payload. M5-E itself does not reinterpret a 304 as a new semantic map-list observation.

## M5-F war-report normalization boundary

The Official War API `warReport/{mapName}` payload contains report metrics only. It does not carry `warId`, canonical region identity, or a source region identifier. M5-F therefore MUST recover both war and map-membership context from durable source evidence before accepting a report.

For a body-bearing report representation observed at `reportObservedAt`:

1. replay the exact durable `region-war-report` source parse and verify parser outcome, structural fingerprint, unknown counters and decoded length;
2. derive the exact case-sensitive `sourceMapName` from the durable endpoint semantic key `war-report/<mapName>`;
3. resolve the latest authoritative `captured_current` `runtime-war-state / war` Fetch with `warFetch.retrievedAt <= reportObservedAt`;
4. require its replayable representation and matching versioned source parse, then invoke M5-D for that exact parse to obtain the canonical `WarId`;
5. resolve the latest authoritative `captured_current` active-map-list Fetch with `mapsFetch.retrievedAt <= reportObservedAt`;
6. replay that exact map-list representation and require an ordinal exact match for `sourceMapName`;
7. resolve the war context at the map-list representation boundary and require it to match the report's `WarId`;
8. invoke M5-E for that exact active-map-list parse and require the exact `(WarId, sourceMapName)` WarRegion;
9. atomically commit the report normalization run and immutable war-report observation.

M5-F does not infer map membership from the existence of a report endpoint alone. Registry discovery proves only that the endpoint was discovered at some earlier point; it is not historical membership evidence.

If the latest authoritative war or map-list context is absent, failed, not yet parsed, or otherwise unconfirmed, normalization is deferred without consuming the report normalizer identity. M5-F does not skip a newer failed source observation to assume an older state remained current.

If the latest replayable map-list representation belongs to a different war than the report war, M5-F defers rather than interpolating membership between independently polled endpoints. When the latest map-list validation is a 304 whose body-bearing representation belongs to the prior war, the result is specifically deferred to M5-G coverage. M5-F MUST NOT fabricate a new map-list payload, source parse, normalization run or WarRegion merely because a 304 proves that the representation bytes were unchanged.

Accepted report observation fields are:

- `totalEnlistments`;
- `colonialCasualties`;
- `wardenCasualties`;
- `dayOfWar`.

All four fields remain nullable because M5 preserves structurally representable source values. Negative values are structurally invalid and are rejected. M5-F does not impose monotonicity or cross-field arithmetic invariants: source corrections/regressions remain valid new immutable observations if individually representable.

`dayOfWar` is source data only. It is not a canonical global war clock, must not replace `observedAt`, and must not be used to fabricate an event timestamp between polls.

A report observation uses the body-bearing report representation Fetch `retrievedAt` as its initial `observedAt`. `recordedAt` remains the local durable database time.

A report observation MUST NOT mutate `runtime.war_regions.first_seen_at`, `last_seen_at`, `source_region_id`, region identity or war identity. Those projections are owned by their respective source evidence.

Accepted M5-F persistence is atomic:

~~~text
normalization_run
    + immutable war_report_observation
~~~

The referenced WarRegion must already be proven through M5-E. A successful normalization run cannot exist without its matching immutable report observation.

Once `(sourceParseRunId, warReportNormalizerVersion)` is durably normalized or rejected, that result is terminal for that version. Replay of a normalized run returns the same durable observation and does not re-resolve a different war or membership context.

A report `304 Not Modified` does not create another body-bearing source parse and therefore does not create a duplicate semantic report observation. Its `source_not_modified` continuity is M5-G coverage work.

## Append-only and mutation rules

Immutable:

- Fetch/Payload evidence;
- source parse runs;
- normalization runs;
- war observations;
- war-report observations.

Bounded mutable identity/projection rows:

- wars;
- regions metadata;
- war_regions discovery metadata.

No mutable projection may erase or replace the observation/evidence chain.

## Recovery and reprocessing

Required behavior:

1. raw Fetch/Payload is durable;
2. source parse is durable;
3. if M5 crashes before normalization, work resumes locally from the parse/evidence rows;
4. if M5 crashes after a normalization run but before canonical commit, retry reconciles idempotently;
5. parser/normalizer upgrades create new versioned derived runs over existing bytes;
6. no recovery path requires replaying the Official War API solely to reconstruct already-durable input.

## First implementation slices

### M5-A — contract foundation

- this specification;
- milestone/status authority updates;
- exact identity/time/provenance invariants.

### M5-B — persistence foundation

- normalization_runs;
- wars;
- regions;
- war_regions;
- war_observations;
- war_report_observations;
- EF migration and migration tests.

### M5-C — normalization kernel

- idempotent normalization-run identity and outcome persistence;
- provenance validation against durable source parse runs;
- generic rejected/failed normalization recording.

### M5-D — war normalization

- durable replay input is resolved by SourceParseRunId to its exact body-bearing representation;
- the matching versioned War API parser is rerun locally over durable bytes;
- replay fingerprint/outcome/unknown counters are checked against durable parse metadata;
- source war identity remains exact, opaque and shard-scoped;
- source lifecycle epoch milliseconds are converted to UTC without fabricated event time;
- unknown winner values are preserved when structurally representable;
- accepted normalization run, war identity and immutable war observation commit atomically;
- runtime.wars is refreshed as a rebuildable projection over immutable observations;
- replay of the same (sourceParseRunId, normalizerVersion) returns the same canonical observation;
- rejected normalization never destroys evidence and never causes an upstream request.

### M5-E — region discovery/membership

- consume only durable `active-map-list` source parse runs and replay the exact body-bearing representation locally;
- validate replay outcome/fingerprint/unknown counters before canonical writes;
- preserve exact case-sensitive `sourceMapName` values; exact duplicates collapse, case variants remain distinct;
- derive the initial canonical region key as `<sourceKey>/map/<exactSourceMapName>` so the global region table does not imply cross-source alias equivalence;
- resolve the war context from the latest authoritative `captured_current` War API fetch for the same shard with `warFetch.retrievedAt <= maps.retrievedAt`;
- exclude `captured_late` / fenced-out responses from context selection even though they remain durable evidence;
- require that selected war source context to provide a replayable body-bearing representation (directly or through 304 lineage) and the matching versioned source parse run;
- invoke M5-D for that exact war source parse so membership is independent of canonical worker ordering;
- if the latest authoritative war fetch is missing, failed, unconfirmed, or not yet parsed, defer without writing a normalization run rather than falling back to older source state;
- never bind earlier map evidence to a future war observation;
- atomically commit `normalization_run + region identities + war_regions` for accepted normalization;
- treat an existing normalization run as terminal for that normalizer version so out-of-order war backfill cannot rebind prior map evidence;
- keep `war_regions` bounded and monotonic: `firstSeenAt=min`, `lastSeenAt=max`, optional `sourceRegionId` may enrich but not conflict;
- absence from a later active-map-list does not delete membership or fabricate a disappearance event;
- successful war normalization opportunistically retries the latest durable maps parse for that shard, closing the normal worker-order race without an upstream refetch.

### M5-F — war-report normalization

- consume only durable body-bearing `region-war-report` source parse runs and replay exact bytes locally;
- verify parser identity, replay outcome, structural fingerprint, unknown counters and decoded length before canonical writes;
- derive exact case-sensitive map identity from `war-report/<mapName>` provenance;
- resolve report war context from authoritative source chronology and invoke M5-D on the exact selected war parse;
- resolve the latest authoritative active-map-list context at the report boundary, replay it, and require exact map membership;
- for a body-bearing map-list validation, require the representation's war context to equal the report war context rather than interpolating across a war boundary;
- invoke M5-E for a body-bearing map-list validation and bind to the exact `(WarId, sourceMapName)` WarRegion;
- for a 304 map-list validation, require an applied M5-G continuity proof for that exact validation Fetch before binding the report to the WarRegion valid at the report war;
- defer incomplete/unconfirmed temporal context without burning the report normalizer identity;
- defer unprocessed cross-war 304 map continuity to M5-G rather than fabricating membership;
- preserve nullable report fields and reject only structurally invalid negative numeric values;
- preserve source corrections/regressions as immutable observations without monotonicity assumptions;
- keep `dayOfWar` as source data, never as a fabricated canonical clock;
- atomically commit `normalization_run + war_report_observation`;
- never mutate WarRegion discovery bounds from report evidence;
- make normalized/rejected `(sourceParseRunId, normalizerVersion)` terminal and replay-stable;
- opportunistically retry latest durable report parses after successful map-list normalization so ordinary worker ordering does not require an upstream refetch.

### M5-G — coverage, recovery and reprocessing verification

M5-G introduces an immutable source-coverage ledger over durable M2/M3 attempt boundaries. Coverage is independent of canonical Foxhole state and MUST NOT be reconstructed from the presence or absence of a canonical observation.

Each terminal durable attempt for the M5 capabilities `runtime-war-state`, `active-map-list` and `region-war-report` is classified exactly once as one of:

| state | meaning |
| --- | --- |
| `observed` | authoritative current body-bearing representation was durably captured and successfully parsed |
| `source_not_modified` | authoritative 304 validated an exact prior body-bearing representation and that representation has a successful source parse |
| `source_unavailable` | an authoritative HTTP response did not provide the expected successful source representation, such as 4xx/5xx |
| `collector_unavailable` | FoxData could not establish a usable observation because collection failed before exchange or while capturing an otherwise successful body |
| `rejected` | source representation exists but the versioned source parser rejected it |
| `unknown` | durable evidence cannot prove source state, including uncertain exchanges, late/superseded captures and orphan 304s |

A missing attempt is not a coverage observation. M5-G MUST NOT manufacture a `collector_unavailable` row for time in which no durable Attempt exists.

For `observed`, provenance is:

~~~text
Attempt
  -> validation Fetch == representation Fetch
  -> body-bearing Payload
  -> versioned SourceParseRun
~~~ 

For `source_not_modified`, provenance is:

~~~text
Attempt
  -> 304 validation Fetch
  -> prior_fetch_id
  -> exact body-bearing representation Fetch
  -> existing or locally repaired versioned SourceParseRun
~~~ 

The 304 validation Fetch and representation Fetch are both retained. A 304 never creates a duplicate Payload or duplicate semantic SourceParseRun.

Coverage `boundaryAt` is the source Fetch `retrievedAt` when a Fetch exists. For collector/uncertain terminal attempts without a Fetch it is the durable attempt completion boundary, falling back only to the attempt start boundary when completion is absent. These local failure boundaries are not source event times.

#### Local parse recovery

If an authoritative 200/304 lineage has durable representation bytes but the expected current parser run is missing, M5-G reruns the exact current parser locally and records the ordinary idempotent SourceParseRun for the same representation Fetch. No upstream request is issued.

The replay uses the same adapter version, parser version, structural fingerprint algorithm, decoding limits and tolerant parsing contract as M3. Reprocessing never mutates raw bytes.

#### 304 active-map continuity

A successful `source_not_modified` observation for `active-map-list / maps` proves that the exact prior map-list representation remained valid at the validation boundary. M5-G may therefore project that unchanged membership set at the 304 boundary, including across a war boundary, provided that:

1. the 304 points directly to the exact body-bearing representation Fetch;
2. the stored SourceParseRun belongs to that representation and is `parsed` or `parsed_with_unknowns`;
3. deterministic parser replay matches outcome, fingerprint, unknown counters and decoded length;
4. every source map name remains a valid exact opaque War API identifier;
5. authoritative `/war` evidence at the 304 validation boundary resolves to a normalized WarId.

This projection reuses the exact representation bytes and exact source parse. It MUST NOT create a synthetic Fetch, Payload, SourceParseRun or region-normalization run.

Instead M5-G transactionally updates the bounded `WarRegion` membership projection for the WarId valid at the validation boundary and records a versioned `coverage_reprocessing_run`. The projection is idempotent on `(coverageObservationId, processorVersion)`.

A successful continuity run has outcome `applied`. Deterministically invalid continuity input such as an invalid source map name or rejected war context records terminal outcome `rejected`. Missing prerequisite war evidence remains deferred and records no terminal run.

M5-F may bind a report through a 304 map context only when the matching validation Fetch has an `applied` M5-G continuity run for the current processor version. Without that durable proof the report remains `map_continuity_requires_coverage`.

#### Canonical reprocessing

M5-G scans durable current source parse runs for the three M5 capabilities whose expected current normalizer version has no terminal normalization run. Candidates are processed in dependency order:

~~~text
runtime-war-state
    -> active-map-list
    -> region-war-report
~~~ 

The existing M5-D/E/F coordinators are invoked by SourceParseRunId. Their versioned idempotency and rejection/defer semantics remain authoritative.

A normalized or rejected result is terminal for that normalizer version. A deferred result creates no replacement identity and remains eligible for a later local pass once its prerequisite durable evidence is available.

The scanner does not issue HTTP requests, does not advance M3 poll state and does not reinterpret late/non-authoritative Fetches as current evidence.

#### Recovery verification requirements

M5-G integration/recovery tests MUST prove at least:

- raw durable 200 with no SourceParseRun can be parsed and normalized locally with no new Fetch;
- repeated recovery is idempotent;
- HTTP source failure, pre-exchange collector failure and uncertain exchange map to distinct coverage states;
- malformed/incompatible source representations become `rejected` coverage and terminal normalizer rejection where applicable;
- 304 stores exact validation-to-representation lineage;
- cross-war map-list 304 first leaves M5-F deferred, then creates the new-war WarRegion through M5-G and permits the same durable report parse to normalize;
- 304 continuity does not create a second source parse or region normalization run;
- report/map/war recovery never depends on current wall-clock state or a new upstream exchange.

### M5-H — completion gate

M5 is complete only after canonical reconstruction from durable M2/M3 evidence is deterministic, provenance-complete, consumer-neutral and independently covered by integration/recovery tests.

The executable completion gate MUST prove all of the following:

1. A representative M5 evidence graph containing war, active-map membership, war report and cross-war 304 continuity can be fully materialized through M5-G.
2. Deleting only M5-derived rows — normalization runs, coverage rows/reprocessing rows and runtime war/region/report state — while preserving M2/M3 Fetch/Payload/Attempt/SourceParseRun evidence does not require another upstream exchange.
3. Re-running local recovery over that preserved evidence reconstructs the same canonical semantics.
4. Rebuild determinism is evaluated over source/natural identity, versioned outcomes, source values, observed boundaries and durable M2/M3 provenance identifiers. Local surrogate UUIDs and local audit timestamps such as `createdAt` / `recordedAt` are storage identities, not semantic equality requirements after deliberate physical deletion and rebuild.
5. Every immutable war/report canonical observation retains an unbroken `NormalizationRun -> SourceParseRun -> representation Fetch -> Payload` chain.
6. Every `observed` / `source_not_modified` coverage row retains the required exact Fetch/representation/parse lineage, and every coverage reprocessing run points to source-not-modified coverage.
7. The M5 canonical Core/Application/Infrastructure boundary remains consumer-neutral: no API, Worker, WarApi source-adapter or Chronicle/UI dependency may enter canonical contracts or persistence.
8. Recovery behavior is independently exercised from the RecoveryTests project, not only through normal integration orchestration.

The completion rebuild compares semantic snapshots and intentionally excludes generated storage IDs and local recording timestamps. Public stability for a future externally exposed identifier is a separate contract and MUST be designed explicitly before M9 rather than inferred from M5 surrogate keys.

Passing M5-H closes the canonical war/region/report milestone. M6 may consume these contracts but MUST NOT retroactively change M5 evidence, time or identity semantics without a new versioned contract/ADR.
