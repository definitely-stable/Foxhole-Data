# M2 hardening plan

Status: in progress.
Baseline: main @ 26b8932325f64942a91bab295627cd19ff80ce60.
Purpose: close only the concrete failure modes confirmed by the post-M2 critical audit before activating real source ingestion.

## Scope and sequencing

The hardening is split into three independently reviewable slices.

### H1 — concurrency correctness

Status: completed in PR #9.

Fix confirmed PostgreSQL concurrency defects before adding any source traffic.

Required changes:

1. establish one row-lock order for M2 state transitions:
   `collection_job -> ingestion_attempt -> endpoint_state`;
2. remove attempt-first/job-second paths from fence acquisition and exchange authorization;
3. make raw capture acquire job/attempt locks in the same order before endpoint state;
4. evaluate lease validity against actual database clock time after lock waits;
5. add deterministic race tests around lease expiry and recovery/authorization;
6. retain Read Committed and explicit row locks; do not introduce Serializable or advisory locks.

Acceptance:

- no M2 path intentionally acquires attempt then job;
- stale lease cannot become `AuthorizedNow` merely because its transaction started before expiry;
- recovery and active worker transactions converge without an application-created lock-order cycle;
- existing M2 tests stay green.

### H2 — lifecycle semantics and retry handoff

Status: completed in PR #10.

Resolve the mismatch between raw evidence durability and later semantic/canonical acceptance.

Required changes:

1. define endpoint authority at M2 as **current raw capture**, not accepted canonical truth;
2. rename `last_authoritative_attempt_id` to `last_current_capture_attempt_id`;
3. keep a collection job as the unit that ends when raw evidence is durably captured;
4. update the five-phase documentation so later normalization/quality/canonical reconciliation uses durable evidence ordering/revision, not the already-released source lease;
5. add explicit pre-exchange abandon/defer transition;
6. add explicit post-authorization uncertain/defer transition;
7. both transitions accept a caller-provided `retryAvailableAt` so M3 can implement source-specific backoff without bypassing M2;
8. the same authorized AttemptId must still never permit a second external exchange.

Acceptance:

- controlled pre-exchange failures do not wait for lease expiry;
- ambiguous post-authorization failures can be persisted immediately;
- retry timing is durable in `collection_jobs.available_at`;
- current-capture metadata cannot be mistaken for quality/canonical acceptance;
- a forward EF migration upgrades existing databases.

### H3 — deployment, CI and evidence integrity

Status: implemented on `m2-hardening/operations-integrity`; verification/merge pending.

Close operational blind spots before M3.

Required changes:

1. add a one-shot EF migration bundle image/service for local Compose;
2. API/Worker depend on successful migration completion, not only PostgreSQL socket health;
3. readiness proves that the compiled EF model has no pending migrations;
4. keep API/Worker free from startup `Database.Migrate()`;
5. CI verifies each test project independently has non-zero discovery, with stronger floors for Integration and Recovery suites;
6. Infrastructure revalidates SHA-256 against exact bytes at the persistence boundary;
7. document separate deployment/schema identity versus runtime identity for production;
8. add a test proving an intentionally mismatched supplied hash is rejected before persistence.

Acceptance:

- clean `docker compose up --build` produces a migrated database;
- an unmigrated reachable PostgreSQL is readiness-unhealthy;
- Migrations run as a one-shot deployment concern;
- losing RecoveryTests or IntegrationTests discovery cannot be hidden by tests from other assemblies;
- persistence cannot accept `hash != SHA256(body)`.

## Non-goals

This hardening does not add:

- real War API HTTP traffic;
- parser/normalizer behavior;
- canonical war/map/objective state;
- S3/MinIO;
- broker infrastructure;
- transparent upstream retry.

## Evidence behind the changes

PostgreSQL 18 distinguishes transaction start time from the actual clock. Lease-validity predicates that must answer “is this lease valid after waiting for locks?” use actual database time, while durable transition timestamps may continue using transaction-consistent time.

PostgreSQL deadlock guidance recommends consistent lock ordering. M2 therefore defines one explicit row-lock order instead of adding deadlock retries around source-exchange authorization.

EF Core deployment keeps migrations separate from normal application startup. Local Compose uses a one-shot migration service; production automation can publish/run the same migration-bundle artifact with a schema-capable deployment identity.
