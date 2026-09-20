# ADR-0001 — Modular monolith

Status: accepted.

Use one codebase/domain model with separate API, Worker and CLI processes over a shared PostgreSQL authority.

Reason:

The system needs strong transactional evidence/reconciliation invariants and currently has no measured throughput or organization boundary that justifies microservices.

Split only after module boundaries and operational measurements prove a service boundary is beneficial.
