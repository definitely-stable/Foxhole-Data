# Research snapshot — 20 September 2026

Non-normative evidence used to choose the architecture.

## Official War API

Repository: https://github.com/clapfoot/warapi

Latest repository commit observed: 26 May 2026.

Important official facts:

- HTTPS/JSON source API;
- Live-1, Live-2 and Live-3 are separate root endpoints;
- Dev data is non-final;
- ETag/If-None-Match explicitly recommended;
- war may update every 60 seconds;
- report/dynamic may update every 3 seconds;
- no documented stable map-item/objective ID;
- undocumented flags must not be relied upon.

Historical issues relevant to defensive design:

- #77 static map data changed during a war historically;
- #81 dayOfWar reset/desync, fixed later;
- #89 ETag not browser-visible because CORS does not expose it, still open;
- #92 partial/mass-NONE dynamic state during server restarts, closed fixed in 2025.

## Foxhole community ecosystem

Catalog:
https://github.com/shayhenderson/FoxholeProjects/blob/main/Projects.md

The catalog's last observed update is November 2024, so it is useful as an inventory but not a complete September 2026 view.

Recurring wrappers listed or discovered:

- Opa-/foxhole-warapi-python-client
- bahildebrand/foxhole-api
- JusteLoneWolf/Foxhole-API-Wrapper
- 3fbaea00/FoxHoleWebAPI
- SICKNICKERONI/foxhole.js
- GoLessn/PyWarApi
- ThePhoenix78/FoxAPI

ThePhoenix78/FoxAPI is a particularly useful current ergonomic reference; latest observed release commit is 1 June 2026. It demonstrates demand for:

- native ETag/cache handling;
- async and sync access;
- map alias normalization;
- composite region/hex data;
- casualty/death-rate helpers;
- captured-town helpers;
- listeners;
- task batching;
- map image generation.

foxhole.js independently demonstrates repeated demand for:

- total casualties;
- victory-town lookup;
- effective required-victory-town calculation;
- claimed victory towns;
- map item/text association;
- flag decoding.

Architectural conclusion:

The ecosystem does not need another thin language wrapper. It benefits from a stable canonical server contract plus generated language clients and separately maintained semantic helpers.

## Reference assets

the-fellowship-of-the-warapi/Assets was observed updated for U66 on 19 August 2026.

Conclusion: reference assets remain an active ecosystem concern but should be a separate adapter/bounded module so runtime-war availability does not depend on asset ingestion.

## Platform standards checked

OpenAPI:
https://spec.openapis.org/oas/v3.2.1.html

OpenAPI 3.2.1 was published 10 September 2026.

ASP.NET Core .NET 10:
https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-10.0

.NET 10 supports native OpenAPI 3.1 / JSON Schema 2020-12 generation. .NET 11 is the first native 3.2 generation line.

.NET:
https://dotnet.microsoft.com/download/dotnet/10.0

Observed current release: .NET 10.0.12, 8 September 2026.

PostgreSQL:
https://www.postgresql.org/docs/18/

Observed current PostgreSQL 18 release: 18.6, 13 August 2026.

Npgsql 10:
https://www.npgsql.org/doc/release-notes/10.0.html

Important direction: PostgreSQL 18 UUIDv7 support and async-first I/O guidance.

AsyncAPI:
https://www.asyncapi.com/docs/reference/specification/v3.1.0

Current checked line: 3.1.0, released 31 January 2026.

CloudEvents:
https://github.com/cloudevents/spec

Stable core release remains 1.0.2.

JSON-RPC:
https://www.jsonrpc.org/specification

JSON-RPC 2.0 remains transport-agnostic and is suitable for the small stdio RPC layer.

Modern stdio precedent:
https://modelcontextprotocol.io/specification/2025-11-25/basic/transports

MCP standardizes JSON-RPC over stdio and Streamable HTTP, explicitly reserving stdout for protocol and stderr for diagnostics. Foxhole-Data borrows the transport discipline, not the MCP application protocol.

Local IPC precedent:
https://learn.microsoft.com/aspnet/core/grpc/interprocess

.NET supports Unix domain sockets and Windows Named Pipes for local IPC. These are optional Foxhole-Data transports, not v1 requirements.

HTTP semantics:
https://www.rfc-editor.org/rfc/rfc9110.html
https://www.rfc-editor.org/rfc/rfc9111.html
https://www.rfc-editor.org/rfc/rfc9457.html
https://www.rfc-editor.org/rfc/rfc9745.html
https://www.rfc-editor.org/rfc/rfc8594.html
https://www.rfc-editor.org/rfc/rfc7240.html

Webhook interoperability:
https://github.com/standard-webhooks/standard-webhooks

Standard Webhooks 1.0 has cross-language verification libraries and observed adoption by multiple major API providers. It is a stronger default than a proprietary signature design.

API design practice:

- Zalando REST guidelines favor opaque cursor pagination for large/mutable collections and document idempotency patterns.
- Google AIP-151 formalizes long-running operation resources.
- Google AIP-158 requires pagination to be designed from the beginning for potentially large collections.

SDK generation:

- Kiota targets C#, Go, Java, PHP, Python, Ruby and TypeScript.
- OpenAPI Generator covers a wider language matrix including Rust and Swift.
- Generator quality is language-specific; select and test per target language instead of forcing one generator everywhere.

Contract governance:

- Redocly CLI supports modern OpenAPI linting/bundling.
- oasdiff actively detects breaking OpenAPI changes and is appropriate for CI.
- AsyncAPI CLI validates AsyncAPI documents.

## Research conclusion

The strongest September 2026 design is not HTTP-only and not wrapper-only.

Use:

- C#/.NET as the server implementation;
- transport-neutral Application semantics;
- OpenAPI-described HTTPS as the universal public query interface;
- JSON-RPC stdio as a first-class local interface;
- durable cursor feeds plus SSE for live consumption;
- Standard Webhooks-compatible outbound push;
- Parquet/NDJSON for bulk history;
- generated transport SDKs plus hand-written helper SDKs;
- PostgreSQL as the initial durable coordination/history store;
- no broker or microservice split before measurements justify it.
