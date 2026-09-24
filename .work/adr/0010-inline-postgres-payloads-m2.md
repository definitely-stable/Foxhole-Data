# ADR-0010 — Inline PostgreSQL raw payloads for M2

Status: accepted for M2.

M2 stores exact raw evidence bytes in PostgreSQL bytea.

External filesystem/S3/MinIO CAS is deferred until source-size and growth measurements justify another durability boundary.

Reasons:

- no real source payload measurements exist yet;
- PostgreSQL TOAST handles large variable-length values;
- one database keeps raw capture transactional;
- external CAS would require object-before-row publication and orphan reconciliation before evidence of need exists.

A future external implementation must preserve PayloadId, SHA-256, byte length and provenance semantics.

## M4 review

Retained after measured M4 source collection by [ADR-0017](0017-m4-retain-inline-postgres-evidence.md). M4 did not find a capacity or durability requirement that justifies an external payload CAS.
