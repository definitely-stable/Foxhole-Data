# Architecture

Status: authoritative.

## Architecture style

Foxhole-Data starts as a modular monolith with separate API, Worker and CLI/stdio processes.

The system is transport-neutral above the transport adapter boundary.

~~~text
Public sources
     |
     v
Source adapters
     |
     v
Ingestion scheduler + endpoint fencing
     |
     v
Immutable evidence
     |
     v
Normalization
     |
     v
Quality gate
     |
     v
Canonical runtime/history
     |
     +------------+-------------+-------------+
     |            |             |             |
     v            v             v             v
Query handlers  Change feed   Exports      Operations
     |
     +-------------------- Application semantics ------------------+
     |             |              |               |                |
     v             v              v               v                v
HTTP REST        stdio RPC       SSE           Webhooks        future IPC/gRPC
~~~

PostgreSQL is the authoritative operational store. Large immutable raw representations MAY be stored in an S3-compatible content-addressed blob store.

## Initial deployable processes

- FoxData.Api
- FoxData.Worker
- FoxData.Cli
- PostgreSQL 18
- optional S3-compatible blob store
- reverse proxy for public deployment

FoxData.Cli owns human CLI commands and machine-oriented stdio RPC mode.

## Logical modules

- Sources
- Ingestion
- Evidence
- RuntimeWar
- Identity
- Quality
- Taxonomy
- Query
- Distribution
- Exports
- Operations

These are code/module boundaries, not initial network services.

## Mandatory invariants

1. Raw evidence is immutable.
2. A state source is not silently represented as an exact event source.
3. Every accepted canonical fact is traceable to evidence.
4. Unknown upstream values survive collection and normalization.
5. ETag is a transport validator, not durable content identity.
6. Exact source content bytes are identified by SHA-256 before application content decoding/storage compression.
7. One durable ingestion attempt performs at most one FoxData application-issued upstream HTTP send. Any later FoxData send requires a new AttemptId; transport-internal connection recovery is governed by ADR-0015.
8. Live shards are separate provenance domains, never replicas for load balancing.
9. Downstream consumers never query internal Foxhole-Data tables directly.
10. Public semantics do not depend on HTTP, stdio, gRPC or any other transport.
11. Public contracts do not depend on C# types or server assemblies.
12. Quality rejection never destroys source evidence.
13. Ambiguous objective identity remains ambiguous.
14. Coverage gaps remain explicit.
15. Durable downstream work uses a transactional outbox.
16. Semantic interpretation is versioned independently from transport.
17. Secondary stores and projections must be rebuildable from authoritative data.
18. New infrastructure requires a measured requirement and an ADR.
19. Source HTTP scheduling/validator state must not duplicate or weaken M2 lease/fence authority.
