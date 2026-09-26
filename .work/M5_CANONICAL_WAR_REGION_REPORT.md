# M5 — Canonical War / Region / Report Model

Status: in progress. M5-A through M5-C implemented; next slice M5-D.
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

- local evidence reader;
- idempotent normalization-run store;
- transaction boundary joining run + canonical write.

### M5-D — war normalization

### M5-E — region discovery/membership

### M5-F — war-report normalization

### M5-G — coverage, recovery and reprocessing verification

### M5-H — completion gate

M5 is complete only after canonical reconstruction from durable M2/M3 evidence is deterministic, provenance-complete, consumer-neutral and independently covered by integration/recovery tests.
