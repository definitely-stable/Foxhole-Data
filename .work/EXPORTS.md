# Bulk exports

REST pagination is not the right interface for large analytical/history extracts.

## Operation model

POST /api/v1/exports

For long-running generation, return:

- 202 Accepted
- Location pointing to /api/v1/operations/{id}
- operation resource with state/progress when meaningful

Prefer: respond-async MAY be honored.

## Formats

Initial planned formats:

- NDJSON for streaming/interoperability;
- CSV for simple tools;
- Parquet for analytical workloads.

Bounded normal JSON exports are allowed only when the result is small.

## Immutable manifest

Every completed export has:

- exportId
- schemaVersion
- canonical data revision
- filters/query
- createdAt
- row count
- file format
- compression
- byte size
- SHA-256
- coverage summary
- quality summary

Export files are immutable once published. Corrections create a new export/data revision.

Exports are produced from canonical data and documented schemas, not by dumping internal tables.
