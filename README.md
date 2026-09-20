# Foxhole-Data

Independent, language-neutral developer data platform for public Foxhole data.

Status: **M2 Evidence Kernel completed**. Next milestone: M3 Official War API Adapter. The current runtime intentionally performs no upstream Foxhole requests; real source ingestion begins in M3.

## Prerequisites

- .NET SDK 10.0.401
- Docker with Compose for PostgreSQL-backed integration tests and the local stack
- Node.js 24 LTS for contract tooling

## Build

~~~bash
dotnet tool restore
dotnet restore FoxData.slnx --locked-mode
dotnet build FoxData.slnx -c Release --no-restore
dotnet format FoxData.slnx --verify-no-changes --no-restore
dotnet test --solution FoxData.slnx -c Release --no-build --minimum-expected-tests 10
~~~

## Local stack

~~~bash
docker compose up --build
~~~

Compose first runs the one-shot EF Core migration bundle, then starts API and Worker only after migrations complete successfully.

The development API listens on http://localhost:8080.

Health endpoints:

- `GET /health/live`
- `GET /health/ready`

## Architecture

The normative architecture and contracts live under [`.work/`](.work/README.md).

Public contract drafts:

- [canonical OpenAPI v1](.work/contracts/openapi/public-v1.yaml)
- [War API compatibility OpenAPI v1](.work/contracts/openapi/compat-warapi-v1.yaml)
- [AsyncAPI events v1](.work/contracts/asyncapi/events-v1.yaml)
- [stdio JSON-RPC v1](.work/contracts/jsonrpc/stdio-v1.md)
