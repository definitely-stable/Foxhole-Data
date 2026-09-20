# ADR-0011 — Explicit leases and endpoint fencing

Status: accepted for M2.

M2 coordinates collection work with durable PostgreSQL rows:

- collection job leaseGeneration bigint;
- lease owner/expiry;
- endpoint fenceToken bigint;
- short row-locking transactions;
- FOR UPDATE SKIP LOCKED for concurrent queue claims;
- conditional UPDATE/RETURNING for ownership-sensitive transitions.

PostgreSQL advisory locks and xmin are not the correctness authority.

Explicit generations remain inspectable after failure and allow deterministic stale-worker rejection.
