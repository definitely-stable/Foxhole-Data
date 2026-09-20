# Security

Threat model emphasizes API resource consumption, SSRF, endpoint inventory and unsafe upstream consumption.

## Upstream HTTP

- fixed host allowlist;
- HTTPS only;
- no user-supplied source URL;
- constrained redirects or redirects disabled;
- connect/header/body timeouts;
- maximum compressed and decompressed bytes;
- parser depth/token limits;
- content-type validation;
- bounded concurrent source requests;
- no transparent retry storm.

## Public API

Read-only public endpoints MAY allow anonymous access with conservative quotas.

API keys MAY provide higher quotas, exports and webhook management.

API keys:

- high entropy;
- prefix-addressable;
- stored hashed;
- scoped;
- rotatable;
- never logged.

Admin/operator plane uses separate authentication and deployment policy.

## PostgreSQL identities

Production schema migration and runtime access use separate identities.

The deployment/schema identity:

- exists only for release/migration execution;
- may perform reviewed DDL and update EF migration history;
- is not mounted into API or Worker runtime environments.

Runtime identities:

- do not own the database schema;
- do not receive CREATE/ALTER/DROP privileges;
- receive only required table/sequence DML;
- should be split further between read-mostly API and ingestion Worker when deployment complexity permits.

For immutable evidence, supported runtime behavior is append/read. Existing `evidence.payloads` and `evidence.fetches` are not application update targets. Production grants SHOULD deny UPDATE/DELETE on those tables except to explicit administrative/retention tooling introduced by a reviewed policy.

Local development may use a single convenience database user; production security MUST NOT infer its privilege model from Compose.

## stdio

stdio trusts the local process/user boundary by default.

stdout is protocol-only; logs and diagnostics go to stderr.

No secrets in diagnostics.

## Webhooks

See WEBHOOKS.md for SSRF, signature and replay controls.

## Resource consumption

Every endpoint has explicit request/body/page/range limits.

SSE has connection and slow-consumer bounds.

Exports have concurrent-operation and storage quotas.

## Inventory

Maintain a machine-readable inventory of every public API version and route.

Debug/experimental/admin endpoints MUST NOT be exposed accidentally in public builds.
