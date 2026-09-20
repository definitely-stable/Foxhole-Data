# Operations

## Health

Public/internal service health endpoints:

- /health/live
- /health/ready

Liveness checks process viability.

Readiness checks mandatory local dependencies such as PostgreSQL and required storage.

Upstream War API unavailability MUST NOT automatically make the query API unready; source freshness is reported separately.

## Source health

Operator views expose:

- last successful fetch;
- source/cache eligibility;
- lag/freshness;
- failure streak;
- schema drift;
- quarantine counts;
- active fence/lease state.

## Private operator plane

/admin/v1 may provide:

- source/endpoint/job inspection;
- bounded manual reschedule;
- quarantine inspection;
- reprocess raw payload with selected parser/normalizer version;
- identity override and revert;
- outbox inspection;
- webhook dead-letter inspection/replay;
- dataset seal/rebuild.

All manual semantic overrides are append-only/audited.

## Backup and recovery

PostgreSQL:

- WAL/PITR;
- off-host backup;
- regular restore drills.

External evidence storage:

- redundancy appropriate to deployment;
- hash verification;
- lifecycle policy that does not delete data still referenced by authoritative DB state.

A database restore is complete only when all required referenced evidence blobs are also available.

## Deployment

Initial deployment should remain simple:

reverse proxy -> FoxData.Api -> PostgreSQL
FoxData.Worker -> PostgreSQL + official sources + optional object store

Do not introduce a broker or orchestrator because it is fashionable.
