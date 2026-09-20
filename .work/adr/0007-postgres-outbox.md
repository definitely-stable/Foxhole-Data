# ADR-0007 — PostgreSQL transactional outbox

Status: accepted.

Canonical mutations and outbound work are committed atomically in PostgreSQL.

Outbox workers use at-least-once delivery and idempotent handlers.

LISTEN/NOTIFY may wake workers but is not durable storage.

Reason:

PostgreSQL already provides the required durability and transaction boundary. A broker is unnecessary until measured fan-out, retention, throughput or independent-consumer requirements justify one.
