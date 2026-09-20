# Platform baseline — 20 September 2026

## Server implementation

Baseline:

- C# 14
- .NET 10 LTS
- ASP.NET Core 10
- .NET Worker Service
- EF Core 10
- Npgsql 10
- PostgreSQL 18.6
- System.Text.Json
- TimeProvider for testable current time
- NodaTime where explicit domain time types improve correctness
- OpenTelemetry for traces, metrics and logs

As of 8 September 2026, .NET 10.0.12 is the current .NET 10 security patch and SDK 10.0.401 carries C# 14. .NET 10 remains supported until November 2028.

Use the newest supported 10.0.x security patch rather than pinning permanently to this snapshot.

PostgreSQL 18 adds uuidv7(), temporal constraints and an asynchronous I/O subsystem. Npgsql 10 supports PostgreSQL 18 UUIDv7 generation and is explicitly moving toward async-first I/O. Application database access therefore MUST be async-first.

## HTTP contract baseline

The latest published OpenAPI Specification is 3.2.1 dated 10 September 2026.

ASP.NET Core 10 natively generates OpenAPI 3.1 and JSON Schema Draft 2020-12. Native OpenAPI 3.2 generation starts with .NET 11.

Therefore:

- executable v1 HTTP contract: OpenAPI 3.1.x;
- schema vocabulary: JSON Schema Draft 2020-12;
- OpenAPI 3.2.1 is tracked as the upgrade target;
- do not maintain a second hand-converted 3.2 copy.

## Event contract baseline

- AsyncAPI 3.1.0
- CloudEvents 1.0.2 JSON format

AsyncAPI describes message/event channels. CloudEvents defines the event envelope. They solve different problems and are complementary.

## HTTP standards

Use:

- RFC 9110 HTTP Semantics
- RFC 9111 HTTP Caching
- RFC 9457 Problem Details
- RFC 9745 Deprecation
- RFC 8594 Sunset
- RFC 7240 Prefer, including respond-async where useful

Rate-limit extension headers MAY be exposed only if their exact draft/version semantics are documented. HTTP 429 plus Retry-After remains the mandatory interoperable behavior.

## Contract tooling

Initial CI direction:

- Redocly CLI for OpenAPI lint/bundle;
- oasdiff for OpenAPI compatibility and breaking-change checks;
- AsyncAPI CLI for AsyncAPI validation;
- JSON Schema validation for event payload schemas.

Tools are implementation choices, not protocol authorities.

## Infrastructure gate

Do not add Redis, Kafka, RabbitMQ, NATS, Kubernetes, Elasticsearch/OpenSearch or a graph database without measured evidence and an accepted ADR.
