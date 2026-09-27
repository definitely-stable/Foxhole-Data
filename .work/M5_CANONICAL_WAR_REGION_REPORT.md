# M5 — Canonical War / Region / Report Model

Status: in progress. M5-A through M5-E implemented; next slice M5-F.
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

### M5-G — coverage, recovery and reprocessing verification

### M5-H — completion gate

M5 is complete only after canonical reconstruction from durable M2/M3 evidence is deterministic, provenance-complete, consumer-neutral and independently covered by integration/recovery tests.
