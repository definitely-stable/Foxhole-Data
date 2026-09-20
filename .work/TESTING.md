# Testing

## Unit

Test:

- time semantics;
- open enum/code handling;
- taxonomy decoding;
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
- duplicate 200;
- unknown additive fields;
- unknown icon;
- unknown flag bits;
- malformed JSON;
- incompatible field type;
- version regression;
- near-empty map;
- mass disappearance;
- mass teamId=NONE;
- war transition;
- pre-conquest/null time fields.

Historical issue 92 fixture is mandatory.

## Integration

Use real PostgreSQL 18 through Testcontainers.

Test:

- migrations;
- uniqueness/temporal constraints;
- leases;
- endpoint fences;
- transaction isolation;
- outbox;
- concurrency;
- recovery;
- export manifests.

## Fault injection

Kill/crash at each durable boundary described in INGESTION_AND_RECOVERY.md.

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

A scheduled non-blocking canary MAY detect upstream contract drift, cache behavior changes and new unknown codes.
