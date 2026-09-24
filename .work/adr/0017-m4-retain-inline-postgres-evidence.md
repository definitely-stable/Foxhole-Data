# ADR-0017 — Retain inline PostgreSQL raw evidence after M4

Status: accepted.
Date: 2026-09-24.
Supersedes: none.
Reviews: ADR-0010.

## Context

ADR-0010 selected PostgreSQL `bytea` for exact raw source payloads during M2 and deferred an external filesystem/S3/MinIO content-addressed store until real source-size and growth measurements existed.

M4 provides that measurement.

The completed 48.169-hour Official War API campaign recorded:

- 317,715 Fetch observations;
- 19,911 HTTP 200 responses;
- 296,168 HTTP 304 validation responses;
- database physical growth of 431,972,352 bytes;
- measured growth of about 215,227,648 bytes/day;
- about 6.46 GB projected over 30 days at the measured workload;
- about 78.56 GB projected over 365 days at the measured workload.

Measured `evidence.payloads` growth was 15,515,648 bytes during the campaign, substantially smaller than Fetch/job/attempt/scheduling metadata growth because 304 validation does not duplicate raw payload bytes.

## Decision

Retain ADR-0010's PostgreSQL-inline raw-evidence design.

Do not add an external filesystem, S3, MinIO or separate CAS durability boundary for M5.

PostgreSQL remains the authoritative transactional durability boundary for exact raw source payload bytes.

## Reasons

- measured raw-payload growth does not justify another storage system;
- conditional validation prevents duplicate payload storage for the dominant 304 traffic;
- one PostgreSQL durability boundary preserves the existing atomic raw-capture model;
- external object storage would add object-before-row publication, orphan recovery, credentials, lifecycle and restore complexity without a demonstrated M4 capacity requirement;
- the measured bottleneck is not raw payload byte growth alone: jobs, attempts, Fetch metadata and scheduling-decision evidence are significant storage contributors.

## Consequences

M5 and later milestones may rely on the existing `PayloadId + SHA-256 + byte length + provenance` semantics without introducing a new storage abstraction solely for capacity.

This decision does not promise indefinite unbounded PostgreSQL retention.

Future work may introduce:

- retention policy;
- table partitioning;
- archival tiers;
- compaction of rebuildable operational data;
- external immutable object storage,

but only from measured product retention, restore, cost or capacity requirements and with a new ADR that preserves evidence identity and provenance.

## Revisit triggers

Re-evaluate this decision if any of the following becomes material:

- measured production growth substantially exceeds the M4 envelope;
- required retention makes PostgreSQL storage or backup/restore cost operationally unacceptable;
- payload size distribution changes materially;
- multi-region durability requires an object-storage boundary;
- export/archive workloads create a separate immutable-blob requirement.

The M4 projection is evidence for the current decision, not a storage SLA.
