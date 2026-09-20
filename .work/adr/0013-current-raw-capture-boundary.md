# ADR-0013 — Raw collection currentness is not canonical acceptance

Status: accepted.
Date: 2026-09-20.

## Context

M2 originally used the endpoint field name `last_authoritative_attempt_id` and the general ingestion specification implied that later canonical reconciliation would re-check the same collection lease/fence after raw capture.

The implemented M2 raw-capture transaction completes the collection job and releases source-collection ownership once exact response evidence is durable.

Using the word "authoritative" for that marker makes it possible to confuse transport/currentness with semantic acceptance. Requiring later quality/canonical processing to reuse a released collection lease is also internally inconsistent.

## Decision

M2 source-collection ownership ends at the raw evidence durability boundary.

The endpoint marker is renamed:

~~~text
last_authoritative_attempt_id
        ->
last_current_capture_attempt_id
~~~

It records the latest raw source observation that was current under the M2 lease/fence rules when captured.

It does not assert:

- successful parsing;
- schema compatibility;
- quality acceptance;
- identity acceptance;
- canonical state mutation.

Normalization, quality, identity and canonical reconciliation are separate durable processing stages over immutable Fetch/evidence records. They use their own processing identity/version/revision and provenance and do not reuse the completed source collection lease/fence.

Controlled failures are recorded explicitly:

- before authorization: fail the attempt and defer the logical job;
- after authorization with ambiguous exchange outcome: mark the attempt uncertain and defer the logical job.

Both paths persist a retry eligibility timestamp. A new source exchange always requires a new AttemptId.

## Consequences

Positive:

- source collection and semantic acceptance have unambiguous meanings;
- quality rejection cannot rewrite raw collection history;
- M3 can persist backoff immediately instead of waiting for lease recovery;
- the same authorized AttemptId remains non-replayable;
- later processing can be independently re-run with newer parser/normalizer versions.

Cost:

- one forward database migration renames the endpoint-state column/index/FK;
- later canonical stages need their own durable processing/revision model rather than borrowing the source lease.

## Rejected alternative

Keeping `last_authoritative_attempt_id` and redefining "authoritative" informally was rejected because the name would continue to imply semantic/canonical acceptance to downstream maintainers.
