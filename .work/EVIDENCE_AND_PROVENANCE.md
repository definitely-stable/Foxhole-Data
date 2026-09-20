# Evidence and provenance

Evidence chain:

source -> endpoint -> fetch -> raw representation -> normalization run -> quality decision -> canonical observation -> observed change

## Raw representation identity

payloadHash = SHA-256(exact original response bytes)

Hash before compression and before parsing.

ETag is stored as transport/source metadata but is not content identity.

## Storage

M2 stores raw payload bytes inline as PostgreSQL bytea and relies on PostgreSQL TOAST for physical large-value handling.

External content-addressed storage remains an allowed future architecture but is not implemented before M4 source-size/growth measurements justify a second durability boundary.

When an external CAS is introduced later:

- a database row MUST NOT reference an external object until that object is durably available;
- object identity remains SHA-256 over exact source bytes;
- storage compression remains metadata, not payload identity;
- existing PayloadId/hash/length/provenance semantics must not change.

Suggested future CAS shape:

sha256/aa/bb/fullhash

## 304 behavior

A 304 creates fetch/coverage evidence and references the prior accepted representation.

Do not store a duplicate body.

Do not create a duplicate semantic observation solely because a validation request occurred.

## Reprocessing

Parser or taxonomy upgrades create a new normalization run over the same raw bytes.

Example:

payload X + parser@1 -> interpretation A
payload X + parser@2 -> interpretation B

Payload X never changes.

Public provenance SHOULD expose stable evidence/revision identifiers without leaking internal storage paths.
