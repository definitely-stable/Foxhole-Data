# Foxhole-Data architecture workspace

Status: normative.

This directory is the architectural authority for Foxhole-Data. The project is an independent developer-facing Foxhole data platform. It MUST NOT depend on the needs, names, contracts, database, deployment or release cycle of any specific downstream application.

Authority order:

1. ARCHITECTURE.md
2. accepted ADRs under adr/
3. subsystem specifications
4. executable contracts under contracts/
5. research/ is evidence only and is never normative

Any implementation PR that changes architecture, public semantics, source interpretation, protocol behavior, persistence invariants or compatibility MUST update the corresponding .work document in the same PR.

Public interoperability is defined by versioned protocols and contracts, not by .NET assemblies or internal database schemas.

Primary documents:

- PRODUCT_SCOPE.md
- ARCHITECTURE.md
- PLATFORM_BASELINE.md
- WAR_API_SEMANTICS.md
- SOURCE_ADAPTERS.md
- TRANSPORTS.md
- STDIO_RPC.md
- HTTP_API.md
- CONTRACT_GOVERNANCE.md
- DATA_MODEL.md
- TIME_SEMANTICS.md
- OBJECTIVE_IDENTITY.md
- QUALITY_AND_COVERAGE.md
- INGESTION_AND_RECOVERY.md
- EVIDENCE_AND_PROVENANCE.md
- CACHING_RATE_LIMITS.md
- EVENTS.md
- WEBHOOKS.md
- EXPORTS.md
- SDK_STRATEGY.md
- HELPERS.md
- SECURITY.md
- OBSERVABILITY.md
- TESTING.md
- OPERATIONS.md
- PERFORMANCE_CAPACITY.md
- M1_REPOSITORY_BOOTSTRAP.md
- M2_EVIDENCE_KERNEL.md
- M2_COMPLETION.md
- IMPLEMENTATION_PLAN.md

Contract sources:

- contracts/openapi/public-v1.yaml
- contracts/openapi/compat-warapi-v1.yaml
- contracts/asyncapi/events-v1.yaml
- contracts/jsonrpc/stdio-v1.md

Research snapshot:

- research/ECOSYSTEM_2026-09.md
- research/M2_STORAGE_CONCURRENCY_2026-09.md
