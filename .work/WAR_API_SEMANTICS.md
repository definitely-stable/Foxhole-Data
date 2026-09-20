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

## Documented endpoints

- GET /worldconquest/war
- GET /worldconquest/maps
- GET /worldconquest/warReport/{mapName}
- GET /worldconquest/maps/{mapName}/static
- GET /worldconquest/maps/{mapName}/dynamic/public

Do not promote undocumented endpoints into the stable source contract.

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

## Known icon and flag behavior

The official icon list currently extends through Update 63, including aircraft-related icon codes 88 through 92.

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

Issue 92 documented truncated dynamic state with mass teamId=NONE during server restarts and was closed as fixed in March 2025. This remains a mandatory regression fixture and anomaly class; it is not treated as a currently active upstream defect.
