# Operations

## Health

Public/internal service health endpoints:

- /health/live
- /health/ready

Liveness checks process viability and MUST NOT depend on PostgreSQL or an upstream source.

Readiness checks mandatory local dependencies and local schema compatibility.

For PostgreSQL, readiness means:

1. the configured database is reachable;
2. EF Core can inspect migration state;
3. the current FoxData build has no pending migrations for that database.

A reachable but unmigrated database is not ready.

Upstream War API unavailability MUST NOT automatically make the query API unready; source freshness is reported separately.

## Database migration deployment

API and Worker MUST NOT run `Database.Migrate()` during ordinary process startup.

Automated deployments use an EF Core migration bundle as a one-shot deployment artifact/job.

Deployment ordering is:

~~~text
PostgreSQL healthy
        ->
migration bundle succeeds
        ->
API / Worker may start
~~~

The migration job is not a long-running service and is not restarted after successful completion.

Local Docker Compose follows the same rule through the `migrate` service.

Production platforms MAY run the same bundle as a deployment job, init-style task or equivalent one-shot release step.

If a deployment requires SQL review/DBA approval instead of automated bundle execution, generate and review an idempotent migration script rather than granting DDL to application processes.

## Database identities

Production separates at least two privilege classes:

### Deployment/schema identity

Used only by the migration deployment step.

May:

- read/write EF migration history;
- create/alter/drop schema objects as required by reviewed migrations.

Its credentials MUST NOT be supplied to API/Worker containers.

### Runtime identity

Used by API/Worker.

It MUST NOT own the schema and normally MUST NOT have CREATE/ALTER/DROP privileges.

Grant only the DML required by the running component.

Raw evidence tables are append/read oriented. Runtime code has no supported path that updates or deletes existing payload/fetch evidence.

Local development Compose may use one convenience PostgreSQL user; that does not define the production privilege model.

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

## Deployment shape

Initial deployment remains simple:

~~~text
reverse proxy -> FoxData.Api -> PostgreSQL
FoxData.Worker -> PostgreSQL + official sources + optional object store

release/deploy job -> EF migration bundle -> PostgreSQL
~~~

Do not introduce a broker or orchestrator because it is fashionable.
