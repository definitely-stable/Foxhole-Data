# Testing

## Unit

Test:

- time semantics;
- open enum/code handling;
- taxonomy decoding;
- source cache eligibility;
- Retry-After parsing;
- ETag syntax/weak validators;
- map-name path safety;
- structural fingerprints;
- deterministic poll spreading;
- quality rules;
- objective matcher;
- state interval transitions;
- observed change detection;
- cursor integrity;
- helper semantics;
- stdio protocol framing/lifecycle.

## Source contract fixtures

Golden fixtures cover every documented official endpoint plus:

- 304;
- repeated 304 against the same body-bearing representation;
- orphan 304;
- duplicate 200;
- strong/weak/invalid ETag;
- unknown additive fields;
- undocumented viewDirection;
- iconType 97;
- unknown icon;
- unknown flag bits;
- opaque map name MarbanHollow;
- exact-case DeadLandsHex;
- Home Region capability asymmetry;
- malformed JSON;
- duplicate JSON property;
- incompatible field type;
- missing/unexpected media type;
- unsupported content encoding;
- oversized wire body;
- decompression expansion overflow;
- version regression;
- near-empty map;
- mass disappearance;
- mass teamId=NONE;
- war transition;
- pre-conquest/null time fields.

Historical issue 92/restart behavior fixture is mandatory.

M3 source tests also prove that parser failure never destroys raw evidence and never triggers an immediate hidden source retry.

## HTTP transport tests

Prove:

- one authorized AttemptId causes at most one FoxData application send;
- redirects are returned, never automatically followed;
- no standard retry/hedging handler is composed;
- fixed source authority cannot be replaced by a user URI;
- ResponseHeadersRead still has a full body deadline;
- Content-Length is not trusted as the only size guard;
- bounded streaming stops at limit + 1;
- automatic decompression is disabled;
- encoded bytes are captured before bounded decoding;
- host cancellation after authorization cannot become a pre-exchange replay;
- network timeout follows uncertain deferral/recovery semantics.

## Integration

Use real PostgreSQL 18 through Testcontainers.

Test:

- migrations;
- uniqueness/temporal constraints;
- leases;
- endpoint fences;
- transaction isolation;
- poll-state separation from fence authority;
- body-bearing representation lineage;
- deterministic successor idempotency;
- concurrent planner reconciliation;
- source parse-run uniqueness/replay;
- outbox;
- concurrency;
- recovery;
- export manifests.

## Fault injection

Kill/crash at each durable boundary described in INGESTION_AND_RECOVERY.md, including M3 parse/successor boundaries.

## Public contract

CI validates:

- OpenAPI 3.1;
- canonical vs implementation OpenAPI;
- breaking changes;
- AsyncAPI 3.1;
- event schemas;
- generated SDK smoke tests;
- examples.

## Transport equivalence

For representative queries, verify HTTP and stdio adapters return semantically equivalent canonical results from the same Application handler.

## Live source canary

Mandatory CI MUST NOT depend on the live War API.

A scheduled non-blocking canary MAY detect upstream contract drift, cache behavior changes, new unknown codes/properties and source availability.

Canary failure never changes the pass/fail result of deterministic mandatory CI.

## CI discovery floors

CI executes every test project independently so discovery failure in one assembly cannot be hidden by successful tests from another.

Current minimum floors are conservative guards, not exact test-count assertions:

~~~text
FoxData.UnitTests         >= 10
FoxData.IntegrationTests  >= 20
FoxData.RecoveryTests     >= 5
FoxData.SourceTests       >= 1
FoxData.ContractTests     >= 2
~~~

Raise a floor when a suite grows materially; do not lower it merely to make an unexpected discovery regression green.

The aggregate number of tests is informative but is not sufficient as the only discovery gate.

## Schema readiness tests

Integration tests MUST verify both:

- a database migrated to the current model is readiness-healthy;
- a reachable PostgreSQL database with pending FoxData migrations is readiness-unhealthy.

## Persistence-boundary integrity

Tests MUST bypass the Application convenience layer at least once and prove that Infrastructure rejects a supplied payload hash that does not equal SHA-256 of the exact bytes.

This prevents the evidence invariant from depending only on one caller behaving correctly.
