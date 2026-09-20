# Data model

Recommended PostgreSQL schemas:

- sources
- ingest
- evidence
- runtime
- identity
- quality
- distribution
- ops

Use UUIDv7 for internal entity IDs. PostgreSQL 18 uuidv7() or application Guid.CreateVersion7() are both acceptable if one ownership strategy is chosen consistently.

## Source registry

sources:

- id
- key
- environment
- adapterVersion
- enabled

shards:

- id
- sourceId
- shardKey
- displayName
- environment

endpoints:

- id
- shardId
- capability
- semanticKey
- sourcePath
- policyId

## Ingestion

jobs:

- id
- endpointId
- scheduledFor
- leaseGeneration
- state

attempts:

- id
- jobId
- attemptNumber
- startedAt
- completedAt
- outcome
- httpStatus
- exceptionClass
- fenceToken

endpoint_poll_state:

- endpointId
- etag
- cacheEligibleAt
- nextTargetAt
- retryEligibleAt
- lastSuccessAt
- fenceToken
- consecutiveFailures

## Evidence

fetches:

- id
- endpointId
- attemptId
- requestedAt
- responseStartedAt
- retrievedAt
- status
- sourceEtag
- cache headers
- payloadId
- priorRepresentationId

payloads:

- id
- sha256
- byteLength
- mediaType
- storageKind
- storageKey
- compression
- createdAt

normalization_runs:

- id
- payloadId
- adapterVersion
- parserVersion
- normalizerVersion
- taxonomyVersion
- startedAt
- completedAt
- outcome

## Runtime

wars:

- id
- source/environment/shard
- sourceWarId
- warNumber
- source fields
- firstObservedAt
- lastObservedAt

Unique natural source identity:

(sourceId, environment, shardId, sourceWarId)

regions:

- id
- canonicalKey
- displayName

war_regions:

- id
- warId
- regionId
- sourceMapName
- sourceRegionId
- firstSeenAt
- lastSeenAt

Immutable accepted observations:

- war_observations
- war_report_observations
- map_observations

## Objective identity layers

- source_occurrences
- objective_instances, within a war
- canonical_objectives, optional cross-war identity
- objective_revisions
- match_runs
- match_candidates
- match_decisions
- manual_overrides

## State intervals

Objective state intervals carry:

- objectiveInstanceId
- property/value or normalized state payload
- observedFrom
- observedUntil, nullable
- openingObservationId
- closingObservationId
- quality
- changeAlgorithmVersion

PostgreSQL range/temporal constraints SHOULD be evaluated for preventing accidental overlap. Do not force EF-only persistence when a PostgreSQL constraint provides a stronger invariant.

## Changes

observed_changes:

- id UUIDv7
- changeSeq bigint monotonic ordering key
- warId
- regionId
- objectiveInstanceId nullable
- type
- previousObservationId
- currentObservationId
- previousObservedAt
- currentObservedAt
- reconstructionBoundaryAt
- previousValue
- currentValue
- evidence references
- identityVersion
- taxonomyVersion
- changeAlgorithmVersion
- quality

## Distribution

outbox:

- id
- eventId
- kind
- payload/schema reference
- createdAt
- availableAt
- attempts
- completedAt

webhook subscriptions and deliveries live in distribution/ops rather than runtime domain tables.
