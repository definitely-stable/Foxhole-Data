# M6 map/taxonomy/quality research snapshot — 2026-09

Status: research evidence only. Not normative.

Purpose: record the source facts and historical incidents used to design M6.

## Official source contract

Primary source:

- https://github.com/clapfoot/warapi
- https://github.com/clapfoot/warapi/blob/master/README.md

Observed current documented behavior:

- map list: `GET /worldconquest/maps`;
- static map: `GET /worldconquest/maps/:mapName/static`;
- dynamic public map: `GET /worldconquest/maps/:mapName/dynamic/public`;
- static and dynamic endpoints share the map-data response shape;
- documented map-level fields:
  - regionId;
  - scorchedVictoryTowns;
  - mapItems;
  - mapTextItems;
  - lastUpdated;
  - version;
- documented item fields:
  - teamId;
  - iconType;
  - x;
  - y;
  - flags;
- documented text fields:
  - text;
  - x;
  - y;
  - mapMarkerType;
- version is documented as a version index that increments when map data changes and is used for caching;
- lastUpdated is documented as a map timestamp;
- static is described as slow/stable data and dynamic as lifecycle-changing public icon data;
- dynamic may update every three seconds;
- ETag / If-None-Match / 304 is the documented cache-validation mechanism;
- current documented icon list extends through icon code 92;
- documented public flag bits are 0x01, 0x02 legacy, 0x04, 0x10 and 0x20;
- unlisted flag bits are explicitly described as internal and not stable for consumers.

Design consequence: FoxData preserves raw values and versions its interpretation. The source documentation is not a safe closed enum contract.

## warapi#77 — static data changed during a war

Source:

- https://github.com/clapfoot/warapi/issues/77

The report records static map data for DeadLandsHex with a version indicating multiple changes during the same war.

Design consequences:

- M6 must not model static data as immutable configuration;
- every body-bearing static representation is durable evidence;
- static remains long-cadence/conditionally revalidated under the M4 policy;
- a static representation change is not automatically a quality failure.

## warapi#92 — restart/mass-NONE malformed dynamic state

Source:

- https://github.com/clapfoot/warapi/issues/92

Historical behavior:

- dynamic data became severely truncated during region-server restart behavior;
- returned items carried teamId NONE;
- consumers generated large numbers of false apparent ownership events;
- the issue was closed in March 2025 as fixed for an update.

The example payload contained a small subset of structures, all with teamId NONE, and version 1.

Design consequences:

- the incident remains a mandatory regression fixture even though upstream reports it fixed;
- a single NONE item cannot be treated as anomalous;
- M6 needs baseline-relative aggregate signals;
- a quarantine must block the representation from replacing the last accepted baseline;
- raw evidence must remain available for future reprocessing.

## warapi#120 — restart flag request / false recent changes

Source:

- https://github.com/clapfoot/warapi/issues/120

The issue describes restart periods producing misleading apparent town ownership transitions and asks for an explicit restart flag. It was closed in March 2025.

Design consequences:

- FoxData cannot depend on a restart flag that is not part of the documented current payload;
- anomaly protection must be evidence-based and versioned locally;
- M6 quality must distinguish source snapshots from inferred change events;
- exact event generation remains M8 work.

## warapi#137 — undocumented iconType 97 and viewDirection

Source:

- https://github.com/clapfoot/warapi/issues/137

Current status at this research snapshot: open.

The issue records a Live-1 dynamic item with:

- iconType 97, beyond the README list ending at 92;
- additive viewDirection.

Design consequences:

- iconType is an open integer;
- a C# closed enum is unsafe as the durable source representation;
- viewDirection is preserved as raw source data;
- M6 taxonomy v1 must not guess semantic meaning for 97 without an accepted taxonomy source;
- unknown icon values alone must not quarantine an otherwise representable snapshot.

## warapi#134 — region/API name inconsistencies

Source:

- https://github.com/clapfoot/warapi/issues/134

Current status at this research snapshot: open.

Examples include API/asset spelling/suffix inconsistencies.

Design consequences:

- exact sourceMapName remains opaque and case-sensitive;
- M6 must not strip/append Hex or infer API names from assets;
- any later human/asset alias layer is versioned interpretation, not source identity.

## warapi#127 — Home Region capability asymmetry

Source:

- https://github.com/clapfoot/warapi/issues/127

Current status at this research snapshot: open.

The issue records that HomeRegionW/HomeRegionC war-report routes exist while ordinary static/dynamic public routes do not.

Design consequences:

- absence of static/dynamic data for Home Regions is not automatically a collection or quality failure;
- endpoint capability planning remains source-specific;
- M6 must not fabricate map observations for unsupported endpoint families.

## warapi#115 — duplicate Rocket Target occurrence

Source:

- https://github.com/clapfoot/warapi/issues/115

Current status at this research snapshot: open.

The issue records duplicate Rocket Target entries at the same location.

Design consequences:

- field equality is not a safe deduplication rule;
- source array occurrences must be preserved independently;
- source ordinal may be retained as representation-local provenance but never promoted to stable objective identity;
- a duplicate-occurrence quality signal may be recorded without deleting either row.

## Coordinate/origin documentation gap

Relevant upstream issues:

- https://github.com/clapfoot/warapi/issues/90
- https://github.com/clapfoot/warapi/issues/91

The official README documents normalized coordinates and constant world extents, but historical issues requested additional region-position/origin clarification.

Design consequences:

- M6 stores source-normalized x/y without strengthening undocumented orientation semantics;
- world-coordinate conversion may be a pure helper;
- coordinates are matching evidence for M7, not M6 identity.

## M4 FoxData measurement evidence

FoxData M4 measured a deterministic three-region Live-1 high-resolution cohort.

For dynamic-map-state:

- 15-second probe baseline;
- 26 observed representation episodes across the three-region eight-hour cohort;
- the simulated 30-second cadence retained 26/26 observed representation episodes in that cohort;
- 60/120-second candidates increased observation delay;
- source version gaps were treated as evidence signals, not counts of proven missed semantic states.

M4 retained:

- recommended dynamic target 30 seconds;
- recommended static target 6 hours with conditional validation;
- executor concurrency 1;
- ETag/cache safety constraints.

Design consequences:

- M6 storage planning must use changed representation count, not request count;
- 304 validation must not duplicate normalized snapshots;
- static must remain revalidated;
- M6 quality calibration may reuse M4 raw evidence where available, but those observations are not universal thresholds.

## External-client corroboration

Generated/open-source War API clients mirror the documented endpoint split between static and dynamic map data. They are useful corroboration but are not source authority.

Source authority remains clapfoot/warapi.

## Design conclusions

The research supports the following M6 decisions:

1. persist source occurrences, not pseudo-objective IDs;
2. preserve duplicates;
3. preserve unknown icon/team/flag/additive values;
4. keep static and dynamic streams separate;
5. normalize one body-bearing representation once;
6. use 304 as validation/coverage, not duplicate source content;
7. separate taxonomy from normalization;
8. separate quality from taxonomy;
9. use accepted prior observations as deterministic anomaly baselines;
10. calibrate mass-NONE thresholds against fixtures/evidence before freezing policy v1;
11. keep issue-92/#120 as permanent regressions even though upstream marked them fixed;
12. defer objective matching and event/change inference to M7/M8.
