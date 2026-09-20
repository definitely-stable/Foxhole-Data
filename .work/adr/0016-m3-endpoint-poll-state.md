# ADR-0016 — M3 poll state is separate from M2 fence authority

Status: accepted.
Date: 2026-09-20.

## Context

M2 intentionally ends source-collection ownership at the raw evidence durability boundary.

Its ingest.endpoint_state contains the endpoint fence, active attempt and last current raw-capture attempt.

M3 additionally needs:

- a body-bearing representation pointer for conditional GET;
- the current ETag validator;
- cache eligibility;
- local cadence target;
- HTTP failure/backoff state;
- a crash-safe marker showing which Fetch has already produced a successor job.

Using last_current_capture_attempt_id for all of these concerns is incorrect.

A current Fetch may be:

- a 304 with no payload;
- a 503/429/error response;
- a valid 200 that later fails parsing.

In particular, M2 requires a 304 prior_fetch_id to reference a Fetch with a Payload, so a chain of 304s cannot simply point at the immediately previous Fetch.

## Decision

M3 introduces a separate durable scheduling/validator projection:

~~~text
ingest.endpoint_poll_state
~~~

It may contain:

- last_processed_fetch_id;
- latest_validation_fetch_id;
- representation_fetch_id;
- validator_etag;
- source_cache_eligible_at;
- next_target_at;
- retry_eligible_at;
- last_http_response_at;
- last_success_at;
- consecutive_failures;
- policy_version;
- updated_at.

Beginning with M4, policy_version identifies both the polling algorithm and the active collection profile, for example:

~~~text
warapi-poll@1/warapi-bootstrap-profile@1
warapi-poll@1/collection-profile@1
~~~

This keeps successor timing reproducible without adding scheduling ownership to M2 endpoint_state.

The table MUST NOT duplicate:

- fence token;
- lease generation;
- active attempt ownership.

Those remain exclusively in the M2 correctness model.

representation_fetch_id points to the body-bearing source representation used for conditional validation. A 304 references that Fetch, not the immediately previous no-body validation Fetch.

Poll state is a rebuildable operational projection over immutable evidence and configured source policy.

Successor scheduling uses deterministic idempotency based on the Fetch that caused planning, for example:

~~~text
after-fetch:{FetchId}
~~~

Poll-state advancement and successor enqueue should be one short PostgreSQL transaction. If its outcome is uncertain, last_processed_fetch_id plus the deterministic enqueue key permit observation-based reconciliation.

## Consequences

Positive:

- ETag validation remains correct across 304 chains and HTTP errors;
- M2 raw-currentness is not confused with reusable HTTP representation state;
- source scheduling can be rebuilt after crashes;
- a committed Fetch cannot permanently silence an endpoint if the process dies before normal successor scheduling;
- M2 lease/fence semantics stay unchanged.

Cost:

- M3 adds a small migration and another operational table;
- planner/reconciler logic becomes a first-class production component rather than a timer-only loop.

## Rejected alternatives

### Store ETag directly in M2 endpoint_state

Rejected because it mixes source HTTP cache state with collection ownership/fencing and still does not solve representation lineage.

### Use the latest Fetch as prior representation

Rejected because the latest Fetch can be a body-less 304 or an error response.

### Schedule the next job only inline after capture

Rejected because a crash after Fetch COMMIT but before enqueue would permanently stop polling that endpoint.
