# ADR-0004 — Separate compatibility and canonical APIs

Status: accepted.

Provide:

- /compat/warapi/v1 for documented source-shaped migration compatibility;
- /api/v1 for stable canonical/history semantics.

Reason:

Existing tools benefit from a low-friction base-URL migration path, while new tools need a model that is not permanently constrained by upstream field names and missing historical semantics.

The two surfaces have independent lifecycle contracts.
