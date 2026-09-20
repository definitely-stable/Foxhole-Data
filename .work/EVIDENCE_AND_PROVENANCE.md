# Evidence and provenance

Evidence chain:

source -> endpoint -> fetch -> raw representation -> source parse run -> normalization run -> quality decision -> canonical observation -> observed change

## Raw representation identity

payloadHash = SHA-256(exact HTTP content bytes supplied to the Evidence Kernel)

For HTTP sources, exact content bytes means the bytes exposed by the HTTP transport after transfer framing is removed and before Content-Encoding decompression.

The hash therefore does not include TCP/TLS records, HTTP chunk/frame bytes or response headers.

M3 disables automatic content decompression so encoded source content cannot be silently transformed before payload hashing.

Hash before parsing and before any application storage compression.

ETag is stored as transport/source metadata but is not content identity.

## Storage

M2 stores raw payload bytes inline as PostgreSQL bytea and relies on PostgreSQL TOAST for physical large-value handling.

External content-addressed storage remains an allowed future architecture but is not implemented before M4 source-size/growth measurements justify a second durability boundary.

When an external CAS is introduced later:

- a database row MUST NOT reference an external object until that object is durably available;
- object identity remains SHA-256 over exact source content bytes;
- storage compression remains metadata, not payload identity;
- existing PayloadId/hash/length/provenance semantics must not change.

Suggested future CAS shape:

sha256/aa/bb/fullhash

## 304 behavior

A 304 creates fetch/coverage evidence and references the body-bearing reusable representation Fetch.

Do not store a duplicate body.

Do not point a new 304 at a previous no-body 304 when the evidence constraint requires a payload-bearing prior Fetch.

Do not create a duplicate semantic observation solely because a validation request occurred.

An orphan 304 with no known body-bearing representation is retained as source evidence but cannot fabricate representation bytes; M3 schedules an unconditional new Attempt.

## Reprocessing

Parser or taxonomy upgrades create a new versioned derived run over the same raw bytes.

Example:

payload X + parser@1 -> source parse A
payload X + parser@2 -> source parse B
source parse B + normalizer@1 -> canonical interpretation C

Payload X never changes.

A crash after raw capture but before parsing is repaired locally from evidence and MUST NOT cause an unnecessary upstream replay.

Public provenance SHOULD expose stable evidence/revision identifiers without leaking internal storage paths.
