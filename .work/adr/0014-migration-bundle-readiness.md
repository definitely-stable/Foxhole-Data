# ADR-0014 — Migration bundles and schema-aware readiness

Status: accepted.
Date: 2026-09-20.

## Context

A PostgreSQL socket-health probe can succeed while the FoxData schema is empty or behind the service binary.

Before M3, Compose could report API readiness on a fresh database because readiness executed only `SELECT 1`. Once Worker source ingestion becomes active, that produces a realistic failure: the service appears healthy and the first M2 registry/queue query fails because the required relation does not exist.

Running migrations automatically from every API/Worker replica would hide deployment ordering and require schema-changing credentials in long-running processes.

## Decision

Automated deployments use an EF Core migration bundle as an explicit one-shot deployment step.

API and Worker never run automatic startup migrations.

Readiness checks the migration state of the configured database and is unhealthy while migrations known to the current service build are pending.

Production uses separate identities:

- deployment/schema identity with reviewed DDL privileges;
- runtime identity without schema ownership or ordinary DDL privilege.

Local Compose uses a convenience development credential but preserves the same execution ordering:

~~~text
postgres healthy
    -> migrate exits 0
    -> api / worker start
~~~

CI verifies the migration job exits successfully before accepting Compose readiness.

## Consequences

Positive:

- reachable-but-unmigrated PostgreSQL can no longer produce false readiness;
- application replicas do not race to migrate the schema;
- deployment credentials do not need to be present in long-running services;
- local Compose exercises the same ordering required by production automation.

Cost:

- deployment now has an explicit migration artifact/job;
- rolling upgrades that require cross-version schema compatibility must design migrations accordingly rather than relying on startup migration timing.

## Operational note

A deployment that requires human SQL review may use reviewed migration scripts instead of the bundle, but the ordering and identity separation remain the same.
