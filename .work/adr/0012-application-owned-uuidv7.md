# ADR-0012 — Application-owned UUIDv7 identifiers

Status: accepted for M2.

Foxhole-Data generates durable M2 entity and operation IDs with .NET Guid.CreateVersion7() before persistence.

PostgreSQL stores them as uuid but does not normally create them with a default.

Reasons:

- stable ID exists before transaction execution;
- unknown COMMIT outcomes can be reconciled by exact ID;
- IDs retain time-ordered UUIDv7 properties;
- tests and production use one strategy.

PostgreSQL 18 uuidv7() remains available for SQL-only administrative use.
