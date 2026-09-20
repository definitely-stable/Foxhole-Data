# Evidence and provenance

Evidence chain:

source -> endpoint -> fetch -> raw representation -> normalization run -> quality decision -> canonical observation -> observed change

## Raw representation identity

payloadHash = SHA-256(exact original response bytes)

Hash before compression and before parsing.

ETag is stored as transport/source metadata but is not content identity.

## Storage

Small payloads MAY be stored inline in PostgreSQL.

Large payloads MAY use external content-addressed storage.

A database row MUST NOT reference an external object until that object is durably available.

Suggested CAS shape:

sha256/aa/bb/fullhash

Compression is storage metadata, not identity.

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
