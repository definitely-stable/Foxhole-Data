# M1 — Repository Bootstrap

Status: implemented in PR #2; awaiting merge.
Prerequisite: M0 architecture foundation.
Successor: M2 evidence kernel.

M1 makes Foxhole-Data a reproducible, testable, observable and deployable .NET repository. It deliberately stops before source ingestion and domain persistence.

## 1. Goals and hard boundary

M1 MUST establish:

- deterministic .NET 10 toolchain;
- SLNX solution and project boundaries;
- package/version/lock-file policy;
- compiler, analyzer and formatting policy;
- Microsoft Testing Platform plus xUnit v3;
- PostgreSQL 18 connectivity and EF Core tooling;
- composition roots for API, Worker and CLI;
- common hosting/OpenTelemetry defaults;
- liveness/readiness health endpoints;
- Docker/Compose local bootstrap;
- OpenAPI/AsyncAPI contract validation;
- build/test/dependency-review CI.

M1 MUST NOT implement:

- War API requests or source DTOs;
- polling, ETags, scheduling, leases or fences;
- evidence/canonical database tables;
- source quality rules;
- public domain API routes;
- JSON-RPC stdio methods;
- SSE, webhooks or exports;
- S3/object storage or a broker.

## 2. Target repository layout

~~~text
/
├─ .config/dotnet-tools.json
├─ .github/
│  ├─ workflows/
│  │  ├─ ci.yml
│  │  ├─ contracts.yml
│  │  └─ dependency-review.yml
│  ├─ dependabot.yml
│  └─ ...
├─ .work/
├─ deploy/docker/
│  ├─ Api.Dockerfile
│  └─ Worker.Dockerfile
├─ src/
│  ├─ FoxData.Core/
│  ├─ FoxData.Application/
│  ├─ FoxData.Infrastructure/
│  ├─ FoxData.Hosting/
│  ├─ FoxData.Sources.Abstractions/
│  ├─ FoxData.Sources.WarApi/
│  ├─ FoxData.Api/
│  ├─ FoxData.Worker/
│  └─ FoxData.Cli/
├─ tests/
│  ├─ FoxData.UnitTests/
│  ├─ FoxData.IntegrationTests/
│  ├─ FoxData.ContractTests/
│  ├─ FoxData.SourceTests/
│  └─ FoxData.RecoveryTests/
├─ tools/contracts/
│  ├─ package.json
│  └─ package-lock.json
├─ .editorconfig
├─ .gitattributes
├─ .gitignore
├─ Directory.Build.props
├─ Directory.Packages.props
├─ FoxData.slnx
├─ global.json
├─ NuGet.config
├─ compose.yaml
└─ README.md
~~~

FoxData.slnx is the only solution file. .NET 10 uses SLNX as the default solution format; carrying SLN and SLNX in parallel creates avoidable drift.

## 3. Production project responsibilities

### FoxData.Core

Pure domain library. M1 contains only an assembly marker. No ASP.NET, EF Core, Npgsql, source adapter or hosting dependency.

### FoxData.Application

Use-case/query/command layer and application ports. References Core only. No transport-specific or persistence-specific types.

### FoxData.Sources.Abstractions

Generic source adapter/capability abstractions. It should remain independent from Application and Infrastructure. It may reference Core only when a real shared primitive is required.

### FoxData.Sources.WarApi

Official source adapter implementation starts in M3. In M1 this project is a compile-time boundary only. No endpoint URLs, DTOs, HttpClient or retry configuration.

### FoxData.Infrastructure

PostgreSQL/EF Core and future durable infrastructure. M1 adds FoxDataDbContext, database registration, options validation, design-time EF factory and database health check. It creates no domain schema.

### FoxData.Hosting

Shared process bootstrap for API, Worker and CLI:

- OpenTelemetry resource/exporter setup;
- common options validation;
- host metadata;
- common health registration;
- logging conventions.

It contains no domain logic and no generic hidden HTTP retry/resilience defaults.

### FoxData.Api

ASP.NET Core Minimal API composition root.

M1 routes only:

~~~text
GET /health/live
GET /health/ready
~~~

No planned domain endpoint is faked simply to match the future canonical OpenAPI contract.

### FoxData.Worker

Generic Host executable. Starts, validates configuration, initializes infrastructure/telemetry and waits cancellation without polling upstream.

### FoxData.Cli

Console executable. M1 reserves CLI structure and output discipline only:

~~~text
foxdata --help
foxdata --version
~~~

Help/version must work without PostgreSQL. JSON-RPC stdio belongs to M10.

## 4. Legal project-reference graph

Allowed:

~~~text
Core -> none

Application -> Core

Sources.Abstractions -> none or Core

Sources.WarApi -> Sources.Abstractions
Sources.WarApi -> optional Core

Infrastructure -> Core
Infrastructure -> Application
Infrastructure -> optional Sources.Abstractions

Hosting -> no FoxData domain/application project

Api -> Application
Api -> Infrastructure
Api -> Hosting

Worker -> Application
Worker -> Infrastructure
Worker -> Hosting
Worker -> Sources.WarApi

Cli -> Application
Cli -> Infrastructure
Cli -> Hosting
~~~

Forbidden examples:

- Core -> Application/Infrastructure/Api/Worker;
- Application -> Infrastructure/Api/Worker;
- Sources.WarApi -> Infrastructure;
- Infrastructure -> executable projects;
- Hosting -> Infrastructure;
- library -> executable project.

CI SHOULD validate ProjectReference edges directly from MSBuild/project files rather than adding an architecture-test package in M1.

## 5. SDK pinning

Initial checked baseline on 20 September 2026:

~~~text
.NET SDK       10.0.401
.NET runtime   10.0.12
C#             14
~~~

global.json:

~~~json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "disable",
    "allowPrerelease": false
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
~~~

SDK changes are explicit dependency-maintenance PRs. A developer with .NET 11 previews installed must not silently build this repository with them.

## 6. Central package management

One root Directory.Packages.props with ManagePackageVersionsCentrally=true.

No nested package-version files in M1.

Do not enable central transitive pinning by default. The exact resolved graph is governed by packages.lock.json.

Initial package baseline:

~~~text
Microsoft.EntityFrameworkCore                         10.0.12
Microsoft.EntityFrameworkCore.Design                  10.0.12
Microsoft.AspNetCore.OpenApi                          10.0.12
Microsoft.Extensions.Diagnostics.HealthChecks             10.0.12

Npgsql                                                10.0.3
Npgsql.EntityFrameworkCore.PostgreSQL                 10.0.3

OpenTelemetry.Extensions.Hosting                      1.19.0
OpenTelemetry.Exporter.OpenTelemetryProtocol          1.19.0
OpenTelemetry.Instrumentation.AspNetCore              1.19.0
OpenTelemetry.Instrumentation.Http                    1.19.0
OpenTelemetry.Instrumentation.Runtime                 1.19.0

xunit.v3                                              4.0.1
Testcontainers.PostgreSql                             4.15.0
Microsoft.AspNetCore.Mvc.Testing                      10.0.12
~~~

Only reference a package where M1 code actually needs it.

NodaTime 3.3.4 is current but SHOULD be deferred until a real domain time type requires it.

Microsoft.Extensions.Http.Resilience MUST NOT be added/configured as a generic source-client default. M3 requires an audited single upstream HTTP exchange per ingestion attempt, without transparent retry or hedging.

## 7. Reproducible restore

M1 enables:

~~~text
RestorePackagesWithLockFile=true
~~~

Every PackageReference project commits packages.lock.json.

CI uses:

~~~text
dotnet restore FoxData.slnx --locked-mode
~~~

Package/SDK updates regenerate lock files in the same PR.

This follows Microsoft's documented reproducible-build guidance: pin the SDK and lock the resolved package graph.

## 8. NuGet security audit

Explicit policy:

~~~text
NuGetAudit=true
NuGetAuditMode=all
NuGetAuditLevel=moderate
~~~

.NET 10 already audits transitive dependencies by default; the explicit properties document repository intent.

Suppression requires a narrowly scoped documented reason. Do not globally disable audit to make CI green.

NuGet.config declares nuget.org as the initial package and audit source.

## 9. Common build policy

Directory.Build.props establishes:

~~~text
TargetFramework              net10.0
LangVersion                  14.0
Nullable                     enable
ImplicitUsings               enable
TreatWarningsAsErrors        true
EnforceCodeStyleInBuild      true
AnalysisLevel                10.0
Deterministic                true
RestorePackagesWithLockFile  true
~~~

ContinuousIntegrationBuild is enabled in CI.

Do not globally enable NativeAOT, trimming, unsafe code, invariant globalization or GC overrides in M1.

## 10. Formatting/analyzers

.editorconfig is the formatting and style authority.

Use built-in compiler/.NET analyzers first. Avoid adding large third-party analyzer packs during bootstrap.

Required CI check:

~~~text
dotnet format FoxData.slnx --verify-no-changes --no-restore
~~~

## 11. Testing platform

Use one platform across all tests:

~~~text
Microsoft Testing Platform
xUnit.net v3 4.0.1
~~~

.NET 10 has native MTP mode and xUnit v3 4.0.1 ships its MTP v2 integration.

Do not mix VSTest and MTP.

Use the xunit.v3.templates 4.0.1 template package when scaffolding the test projects.

CI enforces a non-zero minimum expected test count so broken discovery cannot pass silently.

No coverage-percentage gate in M1; add it after substantive M2+ behavior exists.

## 12. Test project roles

FoxData.UnitTests:
pure fast tests, no Docker/network/database.

FoxData.IntegrationTests:
PostgreSQL 18.6 Testcontainers, Npgsql and DbContext behavior.

FoxData.ContractTests:
repository contract invariants and later canonical/runtime comparison. Redocly/AsyncAPI tooling remains the primary schema validator.

FoxData.SourceTests:
exists in M1 but never contacts the live War API. Becomes substantive in M3 using fixtures.

FoxData.RecoveryTests:
exists as the future crash/fault-injection boundary. Becomes substantive in M2.

Do not create meaningless tests to make an empty project look populated.

## 13. PostgreSQL bootstrap

Baseline:

~~~text
postgres:18.6-bookworm
~~~

Configuration key:

~~~text
ConnectionStrings:FoxData
~~~

Environment form:

~~~text
ConnectionStrings__FoxData
~~~

M1 integration tests prove:

- container starts;
- asynchronous Npgsql connection succeeds;
- server major version is 18;
- FoxDataDbContext.CanConnectAsync succeeds;
- cancellation is honored.

## 14. EF Core migration policy

M1 creates FoxDataDbContext but no domain DbSet and no domain table.

Migration assembly is FoxData.Infrastructure.

The local dotnet tool manifest pins dotnet-ef 10.0.12.

Provide deterministic design-time DbContext creation without having to boot the HTTP API.

Do NOT create an empty InitialCreate migration for ceremony.

The first migration belongs to M2, when source/evidence schema actually exists.

API/Worker startup MUST NOT automatically run migrations. Migration execution is an explicit deployment/operator action.

## 15. Configuration

Use strongly typed Options plus ValidateOnStart for process-required settings.

Initial groups:

~~~text
DatabaseOptions
TelemetryOptions
HostMetadataOptions
~~~

Do not create WarApiOptions before M3.

Rules:

- sealed option types;
- constant configuration section names;
- startup validation;
- environment override support;
- no real secrets in appsettings.json;
- actionable startup errors.

API/Worker need database configuration in normal run mode. CLI help/version must not.

## 16. Host bootstrap

FoxData.Hosting should expose small explicit registration methods, conceptually:

~~~text
AddFoxDataHostDefaults
AddFoxDataTelemetry
AddFoxDataConfigurationValidation
AddFoxDataHealthChecks
~~~

Do not hide application/domain registration inside one giant helper.

Do not adopt generic Aspire ServiceDefaults in M1: Foxhole-Data does not need service discovery, and generic HTTP resilience defaults risk conflicting with later source-ingestion semantics.

## 17. OpenTelemetry

Configure traces, metrics and logs.

Resource attributes include:

- service.name;
- service.version;
- service.instance.id where appropriate;
- deployment.environment.name.

Service names:

~~~text
foxdata-api
foxdata-worker
foxdata-cli
~~~

API instrumentation:

- ASP.NET Core;
- HttpClient;
- runtime/process where useful.

Worker:

- HttpClient;
- runtime/process;
- no fake custom source meters until M2/M3 behavior exists.

Use OTLP as the production-neutral exporter. An OTLP collector is optional in local development; its absence must not break application startup.

Do not add Serilog in M1 unless a concrete gap in Microsoft.Extensions.Logging plus OpenTelemetry is demonstrated.

## 18. Health semantics

API:

~~~text
/health/live
/health/ready
~~~

Liveness checks process viability only.

Readiness checks PostgreSQL and future mandatory local durable dependencies.

Official War API availability is NOT part of API readiness. The query service must remain able to serve stored history during upstream outages.

Health responses do not expose connection strings or sensitive internals.

## 19. HTTP/OpenAPI bootstrap

Use ASP.NET Core Minimal APIs and Microsoft.AspNetCore.OpenApi.

M1 wires runtime OpenAPI generation as a smoke test of the implementation toolchain.

The runtime document is NOT required to equal .work/contracts/openapi/public-v1.yaml yet because M9 owns public domain API implementation.

Do not create fake routes merely to make a contract diff pass early.

Do not add Swagger UI, controllers, auth, CORS, rate limiting, response caching or compression in M1 without a route that actually needs the feature.

## 20. Worker behavior

Worker has no poll loop.

A bootstrap hosted service may wait for cancellation but must:

- consume negligible idle CPU;
- avoid repeated log output;
- stop promptly on SIGTERM/Ctrl+C;
- never contact Foxhole upstream.

## 21. CLI behavior

M1 provides only help/version and process/output conventions.

Normal output -> stdout.
Diagnostics/errors -> stderr.

No database connection for help/version.

No JSON-RPC parser before M10.

Do not add a command framework package until the real CLI surface justifies it.

## 22. Containers

Build separate API and Worker images using multi-stage builds.

Initial base lines:

~~~text
mcr.microsoft.com/dotnet/sdk:10.0.401-noble
mcr.microsoft.com/dotnet/aspnet:10.0.12-noble
mcr.microsoft.com/dotnet/runtime:10.0.12-noble
~~~

Run service containers as non-root.

Production publishing can later pin image digests; M1 exact version tags are sufficient while dependency automation is established.

## 23. compose.yaml

M1 local stack:

~~~text
postgres
api
worker
~~~

PostgreSQL:

- postgres:18.6-bookworm;
- named volume mounted at /var/lib/postgresql;
- pg_isready health check;
- clearly development-only credentials.

PostgreSQL 18 changed the official image PGDATA to /var/lib/postgresql/18/docker and the declared VOLUME to /var/lib/postgresql. Do not use the pre-18 /var/lib/postgresql/data mount target for the v18 image.

No MinIO/S3, reverse proxy or broker in M1.

Compose is a developer bootstrap, not the production orchestration architecture.

## 24. Contract tooling

Keep non-runtime tooling under tools/contracts.

Use Node.js 24 LTS, not Node 26 Current.

Checked baseline on 20 September 2026:

~~~text
Node.js LTS line       24.21.x
@redocly/cli           2.53.3
@asyncapi/cli          6.1.0
~~~

Exact package versions are committed in package.json/package-lock.json and CI runs npm ci.

Required M1 contract checks:

- Redocly lint public-v1.yaml;
- Redocly lint compat-warapi-v1.yaml;
- AsyncAPI validate events-v1.yaml;
- unresolved-reference failure.

oasdiff is wired for future released-contract comparisons. No fake baseline is required before the first contract release.

Never use npx package@latest in required CI.

## 25. .NET CI

.github/workflows/ci.yml runs on pull_request to main and push to main.

Logical required checks:

1. checkout;
2. setup exact .NET SDK from global.json;
3. dotnet --info;
4. restore --locked-mode;
5. dotnet format --verify-no-changes --no-restore;
6. Release build --no-restore;
7. MTP tests with non-zero minimum discovery;
8. PostgreSQL integration tests;
9. build API Docker image;
10. build Worker Docker image.

Mandatory CI does not contact official Foxhole hosts.

Upload test diagnostics on failure.

## 26. Contract CI

.github/workflows/contracts.yml:

1. checkout;
2. setup Node 24 LTS;
3. npm ci in tools/contracts;
4. lint both OpenAPI contracts;
5. validate AsyncAPI;
6. validate any deterministic stdio-contract invariants;
7. optionally upload bundled contracts as artifacts.

M9 adds canonical-vs-runtime OpenAPI equivalence.

## 27. Dependency/supply-chain CI

Enable GitHub dependency graph and dependency review.

Use:

~~~text
actions/dependency-review-action@v4
~~~

Initial vulnerability failure threshold:

~~~text
moderate
~~~

Configure Dependabot for:

- NuGet;
- GitHub Actions;
- npm under tools/contracts;
- Docker.

Dependency Review requires GitHub Dependency Graph to be enabled at repository level. The workflow probes this capability: when the graph is enabled, moderate-or-higher newly introduced vulnerabilities fail the PR; when disabled, the workflow emits an explicit warning and skips the unavailable GitHub service. NuGet Audit remains mandatory regardless of this repository setting.

Do not add a broad license deny-list in M1.

Initial GitHub Actions major versions:

~~~text
actions/checkout@v7
actions/setup-dotnet@v6
actions/setup-node@v7
actions/upload-artifact@v7
actions/dependency-review-action@v5
~~~

Use Node 24-compatible actions. Immutable action-SHA pinning may be added during later supply-chain hardening.

## 28. Root README after M1

Keep it short:

- one-sentence purpose;
- status: bootstrap/pre-ingestion;
- prerequisites;
- Docker Compose quick start;
- local restore/build/test commands;
- links to .work and contracts;
- explicit statement that official War API ingestion starts in M3.

Do not duplicate architecture specifications from .work.

## 29. Clean-checkout verification

Required documented path:

~~~text
dotnet restore FoxData.slnx --locked-mode
dotnet build FoxData.slnx -c Release --no-restore
dotnet format FoxData.slnx --verify-no-changes --no-restore
dotnet test --solution FoxData.slnx -c Release --no-build
docker compose up --build
~~~

Expected after Compose:

- PostgreSQL healthy;
- API running;
- Worker running;
- /health/live healthy;
- /health/ready healthy when PostgreSQL is reachable;
- zero upstream Foxhole requests.

EF tooling:

~~~text
dotnet tool restore
dotnet ef --version
~~~

No migration exists yet.

## 30. M1 implementation slices

### M1.1 — Build skeleton and dependency graph

Deliver global.json, FoxData.slnx, Directory.Build.props, Directory.Packages.props, .editorconfig, NuGet.config, production/test csproj files, legal ProjectReference graph, lock files and dotnet tool manifest.

Acceptance:

- locked restore;
- Release build;
- MTP discovery;
- format verification.

### M1.2 — PostgreSQL infrastructure bootstrap

Deliver FoxDataDbContext, DatabaseOptions, Npgsql/EF registration, design-time factory, Testcontainers fixture and connectivity tests.

Acceptance:

- PostgreSQL 18.6;
- async connectivity;
- no migrations/domain tables.

### M1.3 — Hosting and observability

Deliver FoxData.Hosting, service metadata, OpenTelemetry traces/metrics/logs, OTLP configuration and startup option validation.

Acceptance:

- API/Worker can start without an OTLP collector;
- no high-cardinality custom telemetry.

### M1.4 — API health/OpenAPI bootstrap

Deliver minimal API composition root, health endpoints, PostgreSQL readiness and native OpenAPI generation.

Acceptance:

- liveness independent of DB;
- readiness follows DB;
- no source network access.

### M1.5 — Worker and CLI bootstrap

Deliver cancellation-safe inert Worker and CLI help/version.

Acceptance:

- clean cancellation;
- CLI works without DB;
- stdout/stderr rules respected.

### M1.6 — Docker/Compose

Deliver API/Worker Dockerfiles and PostgreSQL/API/Worker compose stack.

Acceptance:

- docker compose up --build;
- API ready;
- Worker stable;
- app containers non-root.

### M1.7 — Contract tooling

Deliver Node tooling lockfile, Redocly, AsyncAPI CLI and contracts workflow.

Acceptance:

- OpenAPI lint green;
- AsyncAPI validation green;
- no unpinned latest tool.

### M1.8 — CI and supply chain

Deliver .NET CI, dependency review and Dependabot.

Acceptance:

- all required checks green from clean GitHub runners;
- no live War API dependency.

### M1.9 — Documentation and cleanup

Deliver README quick start and remove all template boilerplate.

Acceptance:

- no WeatherForecast/sample routes;
- no placeholder secrets;
- exact M1 baseline recorded;
- full Definition of Done satisfied.

## 31. Required M1 tests/checks

Before closing M1:

1. legal ProjectReference graph passes;
2. invalid required configuration is rejected;
3. PostgreSQL Testcontainer is major version 18;
4. Npgsql async connect succeeds;
5. FoxDataDbContext.CanConnectAsync succeeds;
6. /health/live does not require DB;
7. /health/ready succeeds with DB;
8. /health/ready fails when DB unavailable;
9. API startup makes no external Foxhole request;
10. Worker starts/stops by cancellation without source traffic;
11. CLI --help succeeds without DB;
12. CLI --version succeeds without DB;
13. both OpenAPI contracts lint;
14. AsyncAPI validates;
15. locked restore succeeds;
16. format verification succeeds;
17. API Docker image builds;
18. Worker Docker image builds.

Do not add assertion-only fake tests merely to increase the count.

## 32. Failure semantics

API:

- invalid mandatory configuration -> fail startup;
- DB unavailable -> process may remain live, readiness unhealthy;
- telemetry backend unavailable -> application remains functional.

Worker:

- invalid mandatory configuration -> fail startup;
- no tight retry loop;
- no source operations.

CLI:

- help/version avoid infrastructure startup;
- invalid invocation -> non-zero exit with stderr diagnostics.

## 33. Security defaults

- no secrets committed;
- no production default password;
- dev Compose credentials explicitly development-only;
- non-root service containers;
- minimal GitHub Actions permissions;
- transitive NuGet audit;
- dependency review;
- no source URL from user input;
- no auth middleware before authenticated operations exist;
- no public admin/debug endpoints.

## 34. Deferred decisions

Not M1:

- exact War API HttpClient handlers -> M3;
- collection cadence -> M4 measurement;
- real temporal domain model/NodaTime usage -> when first needed;
- temporal/range DB constraints -> real M2/M8 schema;
- public auth/rate limits -> M9/hardening;
- stdio framework/JSON-RPC -> M10;
- gRPC/UDS/Named Pipes -> measured need;
- S3/MinIO -> M2 evidence requirement;
- production reverse proxy -> deployment work;
- coverage percentage gate -> M2+ substantive code.

## 35. Definition of Done

M1 is DONE only when:

Repository/toolchain:
- FoxData.slnx is the only solution;
- exact .NET 10 SDK is pinned;
- MTP is repo-wide;
- central package management and committed lock files work;
- NuGet audit and formatting policy are active.

Architecture:
- nine production projects and five test projects exist;
- reference graph matches this spec;
- Core/Application remain transport/persistence independent;
- no real War API code exists.

Database:
- PostgreSQL 18.6 test/dev baseline;
- DbContext and design-time tooling work;
- integration connectivity is proven;
- no ceremonial migration;
- no auto-migrate at process startup.

Processes:
- API starts and health semantics are correct;
- Worker starts and cancels cleanly;
- CLI help/version work.

Observability:
- common OTel bootstrap exists;
- OTLP supported;
- no collector is required for local startup.

Containers:
- API/Worker images build;
- Compose starts PostgreSQL/API/Worker;
- app containers are non-root.

Contracts:
- OpenAPI lint green;
- AsyncAPI validation green;
- runtime OpenAPI generation wired;
- no fake public route implementation.

CI/security:
- locked restore/build/format/MTP/integration/Docker checks green;
- dependency review and Dependabot configured;
- mandatory CI performs no War API calls.

Documentation:
- root README gives a working clean-checkout path;
- .work remains authoritative;
- completion records exact baseline versions.

## 36. Handoff to M2

M2 should be able to add source/evidence registry schema and crash-safe persistence without changing:

- project topology;
- test runner;
- package management;
- database provider;
- host bootstrap;
- telemetry architecture;
- health semantics;
- contract-validation foundation.

If M2 immediately requires restructuring these foundations, M1 is not complete.


---

## M1 completion record

Implementation validation baseline:

~~~text
.NET SDK                         10.0.401
.NET runtime                     10.0.12
C#                               14
PostgreSQL                       18.6
Npgsql                           10.0.3
Npgsql EF Core provider          10.0.3
EF Core                          10.0.12
OpenTelemetry                    1.19.0
xUnit v3 / MTP v2                4.0.1
Testcontainers.PostgreSql        4.15.0
Node.js contract tool line       24 LTS
Redocly CLI                      2.53.3
AsyncAPI CLI                     6.1.0
~~~

Validated on GitHub-hosted Ubuntu runners:

- locked NuGet restore succeeds;
- dotnet-ef 10.0.12 restores and executes;
- dotnet format reports no changes;
- Release build succeeds with warnings treated as errors;
- CLI --help and --version execute without PostgreSQL;
- all five test assemblies execute under Microsoft Testing Platform;
- 11 tests succeed, 0 fail;
- PostgreSQL 18.6 Testcontainers integration succeeds;
- both canonical OpenAPI documents pass Redocly validation;
- AsyncAPI document validates;
- Docker Compose configuration validates;
- PostgreSQL, API and Worker start together;
- API /health/ready becomes healthy against PostgreSQL;
- Development runtime OpenAPI is reachable;
- Worker remains running under the generic .NET runtime image;
- Compose teardown removes the bootstrap volume.

Repository security note:

GitHub Dependency Review is wired and capability-gated. The repository-level Dependency Graph is currently disabled, so the GitHub Dependency Review service is skipped with an explicit warning. NuGet transitive vulnerability audit remains enforced independently. Once Dependency Graph is enabled in repository Security settings, the existing workflow automatically enforces moderate-or-higher newly introduced dependency vulnerabilities.

No official Foxhole source request is made by the M1 runtime or mandatory CI.
