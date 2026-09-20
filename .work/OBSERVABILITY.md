# Observability

Use OpenTelemetry traces, metrics and structured logs.

OpenTelemetry .NET traces, metrics and logs are stable and support currently supported .NET versions.

## Required source/data metrics

- source poll lag;
- request outcomes by endpoint kind;
- 200/304 ratio;
- source latency;
- raw/wire bytes and decoded bytes when applicable;
- compression ratio when content encoding is observed;
- source cache delay;
- Retry-After use;
- body/header/decoder limit hits;
- uncertain application exchanges;
- parser outcomes;
- unknown source code/property counts;
- schema fingerprint changes;
- planner repair count;
- successor scheduling lag;
- accepted/suspect/quarantined observations;
- quality-rule hit counts;
- objective identity ambiguity;
- canonical change count;
- coverage gaps.

## Distribution metrics

- API latency/error/429 by route template;
- SSE connection count and slow-consumer closes;
- outbox age;
- webhook delivery latency/failure/retry;
- durable change cursor lag for managed consumers;
- export queue age and generation duration.

## Cardinality

Metrics use low-cardinality dimensions only:

- source;
- environment;
- shard;
- endpoint/capability kind;
- outcome;
- source policy version;
- API route template;
- event type.

Map names, war/objective/evidence IDs, AttemptId, FetchId, ETag and payload hash belong in traces or structured logs, not broad metric labels.

## Trace model

Representative collection trace:

~~~text
planner
 -> claim
 -> begin attempt
 -> fence
 -> authorize
 -> HTTP client span
 -> raw capture
 -> source parse/fingerprint
 -> poll-state/successor reconcile
~~~

Representative later end-to-end trace:

~~~text
source collection -> normalize -> quality -> identity -> reconcile -> outbox -> API/event/export
~~~

Trace context is propagated through internal asynchronous work where possible.

M3 MUST NOT create a second artificial HTTP span for a logical retry hidden inside application code; a new FoxData send requires a new durable AttemptId.

## Logging

Logs are structured.

Never log raw secrets, API keys, webhook secrets or arbitrary full raw payloads by default.

Source logs may include bounded identifiers such as shard/capability/AttemptId/FetchId for diagnosis, but not response bodies or unbounded source content.

stdio RPC diagnostics go only to stderr.
