# Ingestion and recovery

## Durable five-phase protocol

A. Claim logical job in a short transaction and advance job lease generation.

B. Acquire semantic endpoint ownership in a separate short transaction and advance endpoint fence token.

C. Outside PostgreSQL, perform exactly one conditional HTTP exchange, enforce response limits, hash exact bytes and durably publish external CAS when selected.

D. Raw-capture transaction persists fetch metadata and payload/reference and marks the attempt raw-durable.

E. Canonical reconciliation transaction verifies current lease generation and endpoint fence, runs normalization/quality/identity, mutates accepted canonical state, records coverage/changes and writes transactional outbox work.

## Retry rule

No hidden transport retry or hedging against the upstream War API.

A retry is a new durable attempt and a new audited HTTP exchange.

This avoids provenance ambiguity and prevents accidental request multiplication.

## Eligibility

Next fetch eligibility is at least:

max(sourceCacheEligibleAt, configuredTargetAt, retryEligibleAt)

Returned source cache headers win over an earlier local target.

## Measurement before production cadence

Before freezing collection-profile@1, run a 48–72 hour probe on a bounded subset and capture:

- Cache-Control/Expires behavior;
- ETags;
- 200/304 ratio;
- payload sizes;
- compression ratio;
- source change frequency;
- map version behavior;
- latency;
- errors.

Initial conservative target candidates:

- war: 60 seconds;
- maps list: 5 minutes;
- war report: 60 seconds per active region;
- dynamic map: 60 seconds per active region;
- static: first sight plus long conditional revalidation, e.g. 6 hours.

These are hypotheses, not permanent guarantees. A faster profile requires measured upstream/cache behavior and a documented operational reason.

## Unknown commit outcome

A lost database connection during COMMIT is an unknown outcome, not proof of rollback.

Durable operations use stable operation IDs or deterministic uniqueness keys so recovery checks observed state before retrying.

## Crash points

Correctness tests MUST kill processes after:

- HTTP response received;
- external CAS durable write;
- raw-capture COMMIT;
- canonical COMMIT with client-side ambiguity;
- external outbox effect before completion record.

Recovery cannot depend on graceful shutdown.
