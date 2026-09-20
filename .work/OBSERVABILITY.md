# Observability

Use OpenTelemetry traces, metrics and structured logs.

OpenTelemetry .NET traces, metrics and logs are stable and support currently supported .NET versions.

## Required source/data metrics

- source poll lag;
- request outcomes by endpoint kind;
- 200/304 ratio;
- source latency;
- raw bytes and compression ratio;
- schema fingerprint changes;
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

- source
- environment
- shard
- endpoint kind
- outcome
- API route template
- event type

War/objective/evidence IDs belong in traces or structured logs, not broad metric labels.

## Trace model

Representative trace:

schedule -> fetch -> raw capture -> normalize -> quality -> identity -> reconcile -> outbox -> API/event/export

Trace context is propagated through internal asynchronous work where possible.

## Logging

Logs are structured.

Never log raw secrets, API keys, webhook secrets or arbitrary full raw payloads by default.

stdio RPC diagnostics go only to stderr.
