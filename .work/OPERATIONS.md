# Operations

## Health

Public/internal service health endpoints:

- /health/live
- /health/ready

Liveness checks process viability.

Readiness checks mandatory local dependencies such as PostgreSQL and required storage.

PostgreSQL readiness includes schema compatibility: the database must be reachable and have no EF Core migrations pending for the running build. A reachable but unmigrated/outdated database is not ready.

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

Schema migration is an explicit deployment phase and is not performed by API/Worker startup.

For automated deployment, build an EF Core migration bundle and execute it as a one-shot deployment job after PostgreSQL becomes reachable and before API/Worker are considered deployable. Local Docker Compose follows the same ordering with the `migrate` service.

Production identity separation:

- migration/deployment identity: may own/apply schema changes required by reviewed migrations;
- API/Worker runtime identity: only the DML/read privileges needed by runtime behavior;
- runtime identity SHOULD NOT own application tables and SHOULD NOT have general `CREATE`, `ALTER` or `DROP` privileges.

Development Compose intentionally uses one development credential for convenience; it is not the production privilege model.

Do not introduce a broker or orchestrator because it is fashionable.
