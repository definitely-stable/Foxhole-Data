# Foxhole-Data

Independent, language-neutral developer data platform for public Foxhole data.

Status: **M4 Source Measurement completed; M5 Canonical War/Region/Report Model in progress**. Official War API collection is implemented and source behavior is measured; M5 is introducing consumer-neutral canonical state over durable evidence.

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
dotnet test --project tests/FoxData.UnitTests/FoxData.UnitTests.csproj -c Release --no-build --minimum-expected-tests 10
dotnet test --project tests/FoxData.IntegrationTests/FoxData.IntegrationTests.csproj -c Release --no-build --minimum-expected-tests 20
dotnet test --project tests/FoxData.RecoveryTests/FoxData.RecoveryTests.csproj -c Release --no-build --minimum-expected-tests 5
dotnet test --project tests/FoxData.SourceTests/FoxData.SourceTests.csproj -c Release --no-build --minimum-expected-tests 1
dotnet test --project tests/FoxData.ContractTests/FoxData.ContractTests.csproj -c Release --no-build --minimum-expected-tests 2
~~~

## Local stack

~~~bash
docker compose up --build
~~~

Compose first runs a one-shot EF migration bundle and starts API/Worker only after the migration job succeeds.

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
