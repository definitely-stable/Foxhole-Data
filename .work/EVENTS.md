# Events and streaming

## Event contract

AsyncAPI 3.1.0 describes event/message operations.

CloudEvents 1.0.2 JSON format is the public event envelope.

Example event types:

- foxdata.war.started.v1
- foxdata.war.ended.v1
- foxdata.objective.owner-observed-changed.v1
- foxdata.map.revision-observed.v1
- foxdata.report.observed.v1
- foxdata.dataset.sealed.v1

The type name carries a schema major suffix.

## Semantics

Observed transitions MUST use observed wording when exact source event time is absent.

A change event includes, as applicable:

- previousObservedAt;
- currentObservedAt;
- reconstructionBoundaryAt;
- evidence references;
- quality;
- identity/taxonomy/change algorithm versions.

## Durable authority

The durable pull feed is authoritative:

GET /api/v1/changes?after=<cursor>

Consumers recover from interruption using this feed.

## SSE

GET /api/v1/changes/stream

SSE is a low-latency convenience transport over durable ordered changes.

Use event IDs/cursors and Last-Event-ID-compatible reconnection semantics.

Disconnection is not cancellation of durable work.

If the stream cannot retain messages for a slow client, close it cleanly and require cursor catch-up rather than buffering without bound.

## Outbox

Canonical state mutation and outbox insertion commit atomically.

Outbox processing is at-least-once.

External delivery handlers MUST be idempotent.

LISTEN/NOTIFY MAY be used as a wake-up optimization but is never the durable queue.
