# ADR-0005 — Preserve unknown source values

Status: accepted.

Source parsing and canonical normalization MUST preserve unknown/additive values rather than fail or coerce them into a known value.

Where forward compatibility matters, expose both raw source code/value and normalized known meaning.

Reason:

War API icon/flag/schema values evolve. Closed enums in generated/community wrappers create avoidable breakage.
