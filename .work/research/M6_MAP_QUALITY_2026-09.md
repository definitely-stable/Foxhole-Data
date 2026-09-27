# M6 map/taxonomy/quality research snapshot — 2026-09

Status: research evidence only. Not normative.

Normative decisions live in [../M6_MAPS_TAXONOMY_QUALITY.md](../M6_MAPS_TAXONOMY_QUALITY.md).

## Purpose

This snapshot records the external and internal evidence used to design M6.

It deliberately separates:

1. current documented Official War API behavior;
2. historical/open upstream incidents;
3. FoxData measurements and architecture constraints;
4. implementation conclusions.

An upstream issue is evidence that a failure mode has existed. It is not treated as a permanent guarantee that the source still behaves that way.

## Current Official War API documentation

Primary source:

- https://github.com/clapfoot/warapi
- https://github.com/clapfoot/warapi/blob/master/README.md

The current documentation exposes:

~~~text
GET /worldconquest/maps/:mapName/static
GET /worldconquest/maps/:mapName/dynamic/public
~~~

The shared map-data shape documents:

- regionId;
- scorchedVictoryTowns;
- mapItems;
- mapTextItems;
- lastUpdated;
- version.

Map items document:

- teamId;
- iconType;
- normalized x/y;
- flags.

Map text items document:

- text;
- normalized x/y;
- mapMarkerType.

The README documents `version` as a cache/version index that increments when map data changes and `lastUpdated` as map-level epoch-millisecond metadata.

Design consequence:

- `version` is revision/cache metadata, not time;
- `lastUpdated` is map-level source time, not item-event time;
- neither is item/objective identity.

### Static map documentation

The README describes static data as things that never change over the lifecycle of a map and says callers normally need it once per map between World Conquests.

FoxData does **not** elevate this wording into an immutability invariant because upstream issue #77 observed static-map version changes during a war.

Design consequence:

- static data is a low-change snapshot stream;
- every changed body-bearing representation remains durable evidence;
- a changed static representation is not automatically a quality failure.

### Dynamic map documentation

The README describes dynamic/public map data as public map icons that can change over the lifecycle of a map and says the data may update every three seconds.

Design consequence:

- dynamic payload = source snapshot;
- dynamic payload != change event;
- Fetch time != exact capture/change time;
- M6 does not emit objective-change events.

### HomeRegion behavior

The README states that `HomeRegionC` and `HomeRegionW` are returned by the active map list but do not have ordinary map data available in this API version.

Related issue:

- https://github.com/clapfoot/warapi/issues/127

Design consequence:

- missing static/dynamic endpoints for HomeRegion are source capability asymmetry;
- that absence is not itself an M6 collection/quality gap;
- M6 must not fabricate map observations for unsupported endpoint families.

### Teams

The current README documents:

~~~text
NONE
WARDENS
COLONIALS
~~~

FoxData retains the raw string and treats the set as open for forward compatibility.

### Icons

The current README icon table ends at iconType 92.

This is taxonomy evidence, not a safe closed parser enum.

Design consequence:

- parser accepts integer icon codes;
- normalization preserves raw values;
- taxonomy interprets only codes defined by the selected versioned profile;
- unknown codes remain unknown, not rejected.

### Flags

The README documents public bits:

~~~text
0x01 IsVictoryBase
0x02 IsHomeBase (legacy/removed)
0x04 IsBuildSite
0x10 IsScorched
0x20 IsTownClaimed
~~~

The README explicitly warns that unlisted bits are internal and should not be relied upon.

Design consequence:

- always retain raw flag bitmask;
- interpret only documented bits in taxonomy@1;
- retain all other bits as unknownBits;
- never invent semantics for an unknown bit.

### ETag and 304

The README documents ETag support and states that a 304 means the cached data remains the latest representation.

Design consequence:

- validation Fetch and body-bearing representation Fetch remain distinct;
- 304 does not create duplicate Payload/SourceParseRun/map snapshot;
- later war-scoped binding of unchanged bytes requires explicit continuity/context proof.

## Upstream issue evidence

### warapi#77 — static data changed during a war

Source:

- https://github.com/clapfoot/warapi/issues/77
- created 2020-11-18;
- closed 2020-12-17.

The report observed a static map response whose version had advanced during an ongoing war.

Use in M6:

- regression evidence against treating static bytes as immutable;
- changed static representations remain append-only normalized snapshots.

Do not infer:

- current frequency of static changes;
- a specific source-side cause.

### warapi#92 — restart/mass-NONE malformed dynamic state

Source:

- https://github.com/clapfoot/warapi/issues/92
- created 2021-10-11;
- closed 2025-03-20;
- labels include `API Issue` and `Fixed For Next Update`.

The reported Fisherman's Row dynamic representation contained a sharply truncated subset of items and returned `teamId = NONE` for those items. The reporter described false event-log changes during server restarts.

Use in M6:

- mandatory historical regression fixture;
- proof that parser-valid JSON can still be unsafe as accepted canonical state;
- input to the composite mass-NONE/representation-collapse rule family.

Do not infer:

- that the exact bug still occurs in 2026;
- that any high NONE share is invalid;
- that NONE alone is a rejection signal.

Because the issue is closed, M6 uses it as regression evidence rather than as a claim about current source reliability.

### warapi#120 — restart-state enhancement request

Source:

- https://github.com/clapfoot/warapi/issues/120
- created 2023-11-20;
- closed 2025-03-20;
- labels include `API Issue` and `Fixed For Next Update`.

The report describes misleading apparent town ownership changes during region reboot/cooldown and asks for an explicit restarting flag.

Use in M6:

- corroborates #92 as a historical class of restart-related false state;
- supports a multi-signal quality rule rather than a single-field heuristic.

Do not infer:

- that a restart flag exists in every current payload;
- that FoxData may depend on an undocumented restart field.

### warapi#115 — duplicate Rocket Target entries

Source:

- https://github.com/clapfoot/warapi/issues/115
- created 2023-10-16;
- open as reviewed in September 2026.

The issue reports two Rocket Target entries at the same location while only one nearby site was visible.

Use in M6:

- mandatory occurrence-multiplicity regression;
- evidence against deduplication by equal coordinate/icon/team/flags;
- evidence against coordinate/icon hashes as ObjectiveId.

Do not infer:

- whether the duplicate is corruption or two legitimate source records;
- stable identity between either occurrence and later representations.

### warapi#134 — API/map naming inconsistencies

Source:

- https://github.com/clapfoot/warapi/issues/134
- created 2026-02-02;
- open as reviewed in September 2026.

The report describes API/asset spelling, case and suffix inconsistencies.

Use in M6:

- reinforces M5 exact/opaque sourceMapName identity;
- prohibits heuristic case folding or suffix stripping in normalization.

Any later human/asset alias mapping must be a versioned interpretation layer.

### warapi#137 — iconType 97 and additive viewDirection

Source:

- https://github.com/clapfoot/warapi/issues/137
- created 2026-07-07;
- open as reviewed in September 2026.

The issue shows a live dynamic item with:

~~~text
iconType = 97
viewDirection = 0
~~~

while 97 is not present in the current README icon table.

Use in M6:

- mandatory open-icon taxonomy fixture;
- mandatory additive-field fixture;
- direct evidence that "current documented maximum icon number" is not a safe closed schema.

FoxData's current parser already preserves `viewDirection`.

The current `warapi-parser@1` also counts `iconType > 92` as an unknown-code diagnostic.

That parser diagnostic is historical parse metadata only. M6 MUST NOT use `UnknownCodeCount` as taxonomy authority. Semantic known/unknown status is determined by the selected taxonomy version.

A future taxonomy update should not require rewriting historical parser truth unless the parser contract itself changes.

## Coordinate/origin documentation gap

Relevant historical issues:

- https://github.com/clapfoot/warapi/issues/90
- https://github.com/clapfoot/warapi/issues/91

The README documents normalized coordinates and constant world extents, while historical issues requested additional origin/position clarification.

Design consequence:

- M6 stores source-normalized x/y exactly;
- M6 does not strengthen undocumented orientation/origin semantics;
- coordinate conversion can remain a pure helper;
- coordinates are evidence for M7 matching, not identity in M6.

## M4 FoxData measurement evidence

M4 measured a deterministic three-region Live-1 high-resolution cohort.

For dynamic-map-state the recorded probe included:

- 15-second high-resolution baseline;
- 26 observed representation episodes across the three-region eight-hour cohort;
- 30-second simulation retaining all 26 observed episodes in that cohort;
- increasing delay at 60/120 seconds;
- source version gaps treated as evidence signals, not counts of proven missed semantic events.

M4 retained:

- recommended dynamic target around 30 seconds;
- recommended static target around six hours with conditional validation;
- executor concurrency 1;
- ETag/cache safety constraints.

Design consequence:

- M6 storage planning uses changed representation count, not request count;
- 304 validation must not duplicate normalized snapshots;
- static remains conditionally revalidated;
- M4 data may calibrate M6 rules but does not define universal thresholds.

## M5 completion constraints

M5 proves:

- exact durable Fetch/Payload/SourceParseRun provenance;
- exact case-sensitive sourceMapName;
- WarId and WarRegion context;
- explicit coverage states;
- 304 lineage;
- deterministic local recovery.

M6 must consume these guarantees rather than create a second context model.

Any M6 coverage-store generalization must keep M5-H green.

## PostgreSQL concurrency options for quality baselines

M6 quality acceptance introduces a race: two candidates for the same WarRegion/capability must not select inconsistent accepted baselines.

PostgreSQL 18 provides viable primitives including:

- row-level locking;
- transaction-level advisory locks such as `pg_advisory_xact_lock`;
- SERIALIZABLE transactions with serialization-failure retry.

Official PostgreSQL 18 references:

- https://www.postgresql.org/docs/18/functions-admin.html
- https://www.postgresql.org/docs/18/transaction-iso.html
- https://www.postgresql.org/docs/18/applevel-consistency.html

M6 planning intentionally does not freeze one primitive yet.

M6-E must compare:

- correctness across two Worker processes;
- crash cleanup semantics;
- contention at measured update rate;
- retry behavior;
- implementation complexity;
- whether serialization must be scoped by WarRegion + capability.

An in-process `lock`, semaphore or singleton is insufficient.

## Out-of-order replay and the chronology barrier

Quality often compares candidate `tN` against the latest prior accepted observation.

If `t2` is evaluated before a durable `t1 < t2` candidate has a terminal quality result, processing t1 later can change what t2's correct baseline should have been.

M6 therefore needs a chronology barrier.

Recommended v1 behavior:

1. order quality candidates by observedAt plus a deterministic durable tie-breaker;
2. before evaluating candidate C, check whether an earlier eligible normalized candidate for the same WarRegion + capability lacks a terminal quality result under the selected taxonomy/policy versions;
3. if one exists, defer C;
4. serialize concurrent evaluation for the same WarRegion + capability;
5. suspect/quarantined candidates are terminal and no longer block later candidates, but never become accepted baselines;
6. the next candidate uses the latest earlier accepted observation.

This makes chronological execution and crash/out-of-order replay converge to the same baseline chain without rewriting old immutable QualityRuns.

## Quality-design conclusions

### Separate parser, normalization, taxonomy and quality

Parser asks:

> Can the source JSON be represented by the current source parser?

Normalization asks:

> Can the parsed source values be losslessly stored under the M6 structural contract?

Taxonomy asks:

> What does this selected interpretation version know about these raw codes?

Quality asks:

> Is this normalized snapshot safe to admit as accepted canonical map state?

A representation may therefore be:

- successfully parsed;
- successfully normalized;
- partly unknown to taxonomy;
- quarantined by quality;

without losing source evidence.

### Composite restart anomaly

A robust mass-NONE rule uses multiple signals.

Candidate features:

- NONE share;
- owned-team share delta;
- total item-count ratio versus accepted baseline;
- icon/family composition collapse;
- source version regression/reset;
- source lastUpdated behavior;
- coverage/context status.

Exact thresholds are M6-F work and must be calibrated against golden fixtures and measured evidence.

### Do not repair source data

M6 must not:

- clamp invalid coordinates into range;
- replace unknown team with NONE;
- remap unknown icon types to a nearby known type;
- delete duplicate occurrences;
- rewrite a conflicting regionId;
- synthesize missing items from an older snapshot.

Quality can suspect/quarantine a candidate while preserving it intact.

## Golden-fixture inventory required before M6-F completion

At minimum:

1. healthy dynamic baseline before the historical restart anomaly;
2. warapi#92 malformed/restart representation;
3. healthy recovery representation;
4. iconType 97 + viewDirection example from #137;
5. duplicate Rocket Target regression inspired by #115;
6. static representation change regression inspired by #77;
7. unknown flag bits;
8. unknown team value;
9. coordinate boundary and out-of-range fixtures;
10. regionId conflict fixture;
11. no-baseline/war-start neutral fixture;
12. HomeRegion capability-asymmetry fixture.

If an upstream issue contains only partial JSON or screenshots, a FoxData fixture must be marked synthetic/minimized rather than being presented as an original complete upstream payload.

## Evidence quality and limitations

Authority/evidence hierarchy:

1. Official War API README for documented source semantics;
2. preserved FoxData source evidence and M4 measurements;
3. upstream issue reports as operational regression evidence;
4. no semantic inference from screenshots or naming conventions alone.

GitHub issues may be:

- historical;
- fixed;
- incomplete;
- environment-specific;
- observational rather than contractual.

Therefore M6 defaults to:

- preserve raw evidence;
- keep unknown-but-representable values;
- version semantic interpretation;
- calibrate quality from evidence;
- fail closed only where automatic acceptance would fabricate trustworthy canonical state.
