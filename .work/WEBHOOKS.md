# Webhooks

Outbound webhooks are a projection of durable events.

## Compatibility target

Foxhole-Data SHOULD follow Standard Webhooks 1.0 conventions for signing/verification where compatible with CloudEvents payloads rather than inventing an incompatible signature scheme.

Standard Webhooks has active multi-language verification libraries, reducing consumer implementation burden.

## Subscription

A subscription contains:

- id
- endpoint URL
- event type filters
- optional shard/war/region filters
- enabled state
- signing secret/key version
- created/updated timestamps

## Delivery

Each delivery attempt records:

- event ID
- subscription ID
- delivery ID
- attempt number
- request timestamp
- response status
- latency
- error classification
- next retry
- terminal/dead-letter state

Receivers should acknowledge quickly and process asynchronously.

## Security

- HTTPS required for public targets;
- endpoint validation before activation;
- DNS/IP checks and revalidation to mitigate SSRF/rebinding;
- block loopback, link-local, private/reserved targets unless an explicit trusted private deployment mode enables them;
- redirect policy is constrained and revalidated;
- signatures verified over exact delivered bytes;
- timestamp/replay window;
- constant-time signature comparison in receiver helpers;
- secret rotation with overlapping key IDs.

## Retry

Use bounded exponential backoff with jitter.

Delivery is at-least-once; consumers deduplicate by stable event/delivery ID.

Repeated terminal failures enter dead-letter state.

Provide explicit delivery history and replay endpoints.

Webhook failure MUST NOT block canonical ingestion.
