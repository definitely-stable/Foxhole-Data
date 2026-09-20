# Official War API source semantics

Normative for the initial source adapter. Source authority is the official clapfoot/warapi repository.

## Source roots

Live source roots:

- Live-1: https://war-service-live.foxholeservices.com/api
- Live-2: https://war-service-live-2.foxholeservices.com/api
- Live-3: https://war-service-live-3.foxholeservices.com/api

Dev:

- https://war-service-dev.foxholeservices.com/api

Dev data is explicitly documented as non-final and subject to frequent change. It MUST be stored in a distinct environment/provenance domain and MUST NOT contaminate live canonical history.

The official source itself is HTTPS/JSON only. This restriction applies to source ingestion, not to Foxhole-Data output transports.

Production source roots are adapter-owned fixed values. Arbitrary runtime/user supplied roots are not part of the M3 contract.

## Documented endpoints

- GET /worldconquest/war
- GET /worldconquest/maps
- GET /worldconquest/warReport/{mapName}
- GET /worldconquest/maps/{mapName}/static
- GET /worldconquest/maps/{mapName}/dynamic/public

Do not promote undocumented endpoints or feature requests into the stable source contract.

Upstream issue #141, updated in September 2026, requests future next-war-start information. It remains a feature request and is not an M3 source endpoint.

## War fields

The current official contract includes:

- warId
- warNumber
- winner
- conquestStartTime
- conquestEndTime
- resistanceStartTime
- scheduledConquestEndTime
- requiredVictoryTowns
- shortRequiredVictoryTowns

warNumber is explicitly shard-scoped.

Foxhole-Data source identity is therefore:

(source, environment, shard, sourceWarId)

even if sourceWarId is observed to be globally unique in practice.

## Update semantics

Official documentation states:

- war may update every 60 seconds;
- war report may update every 3 seconds;
- dynamic map data may update every 3 seconds;
- static data is intended to be requested once per map between World Conquests.

Actual polling MUST also honor returned cache headers and ETags.

M3 intentionally starts report/dynamic polling more conservatively and defers any faster measured profile to M4.

## Map-name semantics

mapName is an opaque, case-sensitive source identifier.

M3 MUST NOT:

- append/remove the Hex suffix;
- change case;
- infer an API name from an asset filename;
- merge two source names based on human similarity.

Upstream issue #134, open in 2026, documents API/asset naming inconsistencies such as MarbanHollow and Deadlands/DeadLands variants.

A map name used to derive a request must also pass bounded single-path-segment safety validation. Unsafe/unbounded values remain in raw evidence and are not silently converted into URLs.

The official documentation historically notes that HomeRegionC and HomeRegionW can appear in the map list while static/dynamic map data is unavailable. Upstream issue #127 also records capability asymmetry for Home Regions. Treat this as a source capability quirk, not a naming normalization rule.

## Map state semantics

Map payloads are snapshots.

Documented fields include:

- regionId
- scorchedVictoryTowns
- mapItems
- mapTextItems
- lastUpdated
- version

version increments when map data changes and is useful for caching. It is not a timestamp.

lastUpdated is map-level source metadata. It is not a per-objective event timestamp.

No stable map-item/objective identifier is documented. Array position is not identity.

Normalized coordinates use the documented constant world extents, but coordinate origin/orientation beyond the official documentation MUST NOT be strengthened into a source guarantee.

## Open values and undocumented additive fields

The official README's current icon list extends through Update 63 and icon code 92.

That list MUST NOT be implemented as a closed runtime enum.

Upstream issue #137, created July 2026 and updated August 2026, documents a Live-1 dynamic item with iconType 97 and an additive viewDirection field while the README still ends at 92.

Therefore:

- raw iconType integers are preserved;
- raw teamId/winner strings are preserved;
- unknown JSON properties are tolerated and retained in parsed diagnostics where practical;
- raw payload bytes remain the ultimate evidence;
- structural fingerprint changes are signals, not automatic rejection.

Documented public flag bits:

- 0x01 IsVictoryBase
- 0x02 IsHomeBase, removed in Update 29
- 0x04 IsBuildSite
- 0x10 IsScorched
- 0x20 IsTownClaimed

The official documentation states that unlisted flags are internal and must not be relied upon. Foxhole-Data MUST preserve the raw bitmask but MUST NOT assign meaning to undocumented bits.

## Required defensive behavior from historical source incidents

Issue 77 showed static map data could historically change during a war. Therefore static data is conditionally revalidated on a long cadence rather than treated as mathematically immutable.

Issue 81 documented map day count reset/desynchronization. dayOfWar MUST NOT be used as the sole canonical elapsed-war clock.

Issue 89 remains open and documents that browsers cannot reliably access the upstream ETag because it is not exposed through CORS. Foxhole-Data browser-facing APIs MUST expose their own ETag and selected metadata headers through CORS.

Issue 92 and issue 120 documented truncated/restarting dynamic state with mass teamId=NONE and false apparent changes; both were closed as fixed in March 2025. These remain mandatory regression fixtures and later quality anomaly classes; they are not treated as currently active upstream defects.

Issue 105 documents a historical server failure mode for malformed/non-scalar If-None-Match construction and was closed fixed in 2023. M3 still emits only validated single entity-tag validators.
