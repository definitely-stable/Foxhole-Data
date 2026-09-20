# M2 storage and concurrency research — 20 September 2026

Status: non-normative evidence.

## PostgreSQL 18.6

Current docs:
https://www.postgresql.org/docs/18/

### UUIDv7

https://www.postgresql.org/docs/18/functions-uuid.html
https://www.postgresql.org/docs/18/datatype-uuid.html
https://learn.microsoft.com/dotnet/api/system.guid.createversion7

Decision: application generates UUIDv7 before persistence so unknown transaction outcomes can be reconciled by stable ID.

### Work claiming

https://www.postgresql.org/docs/18/sql-update.html

PostgreSQL documents FOR UPDATE plus SKIP LOCKED as useful for multiple commands contending for work rows.

Decision: indexed candidate CTE + FOR UPDATE SKIP LOCKED + UPDATE RETURNING.

### Concurrency

https://www.postgresql.org/docs/18/mvcc.html
https://www.postgresql.org/docs/18/applevel-consistency.html

Decision: default Read Committed plus explicit row locks/conditional updates. No global Serializable mode.

### Advisory locks

https://www.postgresql.org/docs/18/functions-admin.html

Decision: no advisory-lock correctness dependency. Durable lease/fence rows remain inspectable after process failure.

### Database time

https://www.postgresql.org/docs/18/functions-datetime.html

Decision: PostgreSQL transaction time controls lease expiry.

### bytea / TOAST

https://www.postgresql.org/docs/18/storage-file-layout.html
https://www.postgresql.org/docs/18/lo-intro.html
https://www.postgresql.org/docs/18/functions-binarystring.html

TOAST automatically stores sufficiently large variable-length values out-of-line; TOASTed fields can be up to 1 GB.

Decision: M2 raw evidence is bytea. No PostgreSQL Large Objects and no external object storage before M4 measurement.

### ON CONFLICT

https://www.postgresql.org/docs/18/sql-insert.html

Decision: use uniqueness plus ON CONFLICT selectively for idempotent operations. Payload hash conflicts still verify actual byte equality.

## EF Core

https://learn.microsoft.com/ef/core/saving/transactions

EF Core notes manually controlled transactions and implicitly retrying execution strategies do not compose transparently.

Decision: Evidence Kernel has no hidden transaction retry. Unknown COMMIT outcomes are observed through stable IDs.

## Npgsql

https://www.npgsql.org/efcore/modeling/concurrency.html

Npgsql supports xmin as an EF concurrency token.

Decision: xmin is not leaseGeneration/fenceToken. Those are explicit bigint domain counters.

## Conclusion

M2 requires no Redis, broker, distributed-lock service or external blob store.

Use PostgreSQL 18, application UUIDv7, row locking/SKIP LOCKED, explicit lease/fence generations, durable exchange authorization, bytea+SHA-256 evidence and observation-based recovery.
