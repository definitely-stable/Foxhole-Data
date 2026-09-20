# Canonical HTTP API design

Base path:

/api/v1

Compatibility facade:

/compat/warapi/v1

Private operator surface:

/admin/v1

## Representation rules

- JSON field names use lowerCamelCase.
- Canonical timestamps use UTC RFC 3339/ISO-8601 strings.
- Source numeric epoch timestamps are preserved only on explicit source-shaped/compatibility models.
- IDs are opaque.
- Stable machine tokens are never localized.
- Unknown source values remain representable.
- Single resources are direct typed resources, not wrapped in a universal success envelope.
- Collection responses are named resource-specific objects with items and nextCursor.

## Errors

Use RFC 9457 application/problem+json.

Stable extensions:

- code
- traceId
- errors, for field validation when useful
- retryable, only when the server can state this meaningfully

No stack traces.

## Conditional requests

GET endpoints SHOULD emit ETag when representation identity is stable.

If-None-Match MUST be honored.

Future mutation/admin endpoints SHOULD use If-Match where optimistic concurrency prevents lost updates.

## CORS

Browser-facing deployment MUST be able to expose:

- ETag
- Retry-After
- Deprecation
- Sunset
- selected X-FoxData-* metadata headers

This explicitly avoids the browser limitation seen on the upstream War API ETag header.

## Initial endpoint catalogue

Metadata:

- GET /api/v1
- GET /api/v1/capabilities
- GET /api/v1/shards
- GET /api/v1/maps
- GET /api/v1/taxonomies
- GET /api/v1/taxonomies/{version}
- GET /api/v1/icons/{code}
- GET /api/v1/flags/decode?value=

Wars:

- GET /api/v1/wars
- GET /api/v1/wars/{warId}
- GET /api/v1/shards/{shard}/wars/current
- GET /api/v1/wars/{warId}/summary
- GET /api/v1/wars/{warId}/casualties
- GET /api/v1/wars/{warId}/victory-towns
- GET /api/v1/wars/{warId}/ownership
- GET /api/v1/wars/{warId}/coverage

Regions:

- GET /api/v1/wars/{warId}/regions
- GET /api/v1/wars/{warId}/regions/{regionId}
- GET /api/v1/wars/{warId}/regions/{regionId}/snapshot
- GET /api/v1/wars/{warId}/regions/{regionId}/snapshot?at=
- GET /api/v1/wars/{warId}/regions/{regionId}/reports

Objectives:

- GET /api/v1/wars/{warId}/objectives
- GET /api/v1/wars/{warId}/objectives/{objectiveId}
- GET /api/v1/wars/{warId}/objectives/{objectiveId}/history

Changes:

- GET /api/v1/changes?after=&limit=
- GET /api/v1/wars/{warId}/changes?after=&limit=
- GET /api/v1/changes/stream

Exports:

- POST /api/v1/exports
- GET /api/v1/operations/{operationId}
- GET /api/v1/exports/{exportId}

Webhooks:

- POST /api/v1/webhook-subscriptions
- GET /api/v1/webhook-subscriptions
- GET /api/v1/webhook-subscriptions/{id}
- PATCH /api/v1/webhook-subscriptions/{id}
- DELETE /api/v1/webhook-subscriptions/{id}
- GET /api/v1/webhook-subscriptions/{id}/deliveries
- POST /api/v1/webhook-subscriptions/{id}/deliveries/{deliveryId}/replay

## Compatibility facade

Explicit shard in every path:

- GET /compat/warapi/v1/{shard}/worldconquest/war
- GET /compat/warapi/v1/{shard}/worldconquest/maps
- GET /compat/warapi/v1/{shard}/worldconquest/warReport/{mapName}
- GET /compat/warapi/v1/{shard}/worldconquest/maps/{mapName}/static
- GET /compat/warapi/v1/{shard}/worldconquest/maps/{mapName}/dynamic/public

Compatibility bodies preserve documented upstream shape. Foxhole-Data-specific provenance is placed in response headers, not inserted into source JSON.

Suggested headers:

- X-FoxData-Shard
- X-FoxData-Observed-At
- X-FoxData-Representation-Sha256
- X-FoxData-Source-ETag
- X-FoxData-Source-Version, when meaningful

## Pagination

Potentially unbounded lists use opaque cursor pagination from day one.

Cursor state is integrity-protected and bound to relevant filter/sort state. Clients MUST NOT parse cursors.

Every paged operation declares default/max page size and deterministic ordering with a unique tie-breaker.

## Long-running operations

Exports and other expensive operations return 202 Accepted with an operation resource and Location header when asynchronous handling is used.

Prefer: respond-async MAY be honored.

## Lifecycle

Within /api/v1:

- additive optional fields are normally compatible;
- removing/renaming/changing semantics is breaking;
- enum expansion MUST be survivable by generated SDKs;
- cursor format is opaque.

Deprecation uses RFC 9745 Deprecation and RFC 8594 Sunset where a removal date exists.
