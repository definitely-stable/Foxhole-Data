# Ingestion and recovery

## Durable collection and interpretation protocol

Foxhole-Data separates source collection correctness from later semantic interpretation.

### A. Claim collection job

Claim the logical collection job in a short PostgreSQL transaction and advance its lease generation.

The collection lease protects only the current source-collection attempt.

### B. Acquire endpoint fence

Begin an ingestion attempt and acquire endpoint ownership in a separate short transaction.

All M2 state-changing transactions use the lock order:

~~~text
collection_job -> ingestion_attempt -> endpoint_state
~~~

Endpoint fence tokens are monotonic and are never reused or decremented.

### C. Authorize and perform exactly one application source exchange

Persist exchange authorization before any external request.

Outside PostgreSQL, perform at most one FoxData application-issued source exchange for that AttemptId.

For HTTP this means one SendAsync/HttpMessageInvoker send invocation. ADR-0015 records the .NET transport boundary and explains why transport-internal connection recovery is not modeled as a second FoxData exchange.

Only a fresh AuthorizedNow result grants permission to issue the application exchange. An AlreadyAuthorized result is observation-only and MUST NOT cause another FoxData request.

No database transaction spans network I/O.

### D. Raw-capture transaction

Persist fetch metadata and exact payload/reference as immutable evidence.

If the collection lease generation and endpoint fence are still current at capture time:

- classify the observation as CapturedCurrent;
- complete the collection job;
- clear its source-collection lease;
- clear the endpoint's active attempt;
- set endpoint_state.last_current_capture_attempt_id.

If the response is stale, preserve it as CapturedLate without advancing the current-capture marker.

last_current_capture_attempt_id means only:

> the most recent raw source observation that was current under the M2 collection lease/fence rules when it became durable.

It does NOT mean that the payload parsed successfully, passed quality checks, matched an identity, became accepted canonical state, or became the reusable HTTP validator representation.

The source-collection lease/fence lifecycle ends at this raw-durability boundary.

### E. Source parse and poll reconciliation

M3 adds durable source parsing/fingerprinting and poll-state reconciliation over immutable Fetch/evidence records.

These stages:

- use their own versioned processing identity;
- distinguish latest Fetch from body-bearing reusable representation;
- may create deterministic successor jobs;
- do not reuse the already-released M2 lease/fence;
- are repairable from durable Fetch records after crash.

### F. Semantic/canonical reconciliation

Normalization, quality, identity, accepted canonical state, coverage, changes and outbox work are later durable processing stages over immutable Fetch/evidence records.

They MUST NOT attempt to reuse the already-released source collection lease or endpoint fence from phases A-D.

Later stages use their own processing identity/version/revision and provenance back to the Fetch/Attempt that supplied the evidence.

A quality rejection therefore cannot retroactively change the historical fact that a raw representation was the current source capture at collection time.

## Controlled failure transitions

### Before exchange authorization

If request construction, configuration, policy or another deterministic pre-exchange step fails:

~~~text
DeferBeforeExchange(
  attemptId,
  leaseGeneration,
  retryAvailableAt,
  errorClass,
  errorCode)
~~~

The transition:

- is safe because no source request was authorized;
- marks the attempt failed / abandoned_before_exchange;
- releases the job lease;
- clears that attempt from active_attempt_id when present;
- requeues the job as pending;
- persists the caller-selected available_at = retryAvailableAt.

It avoids waiting for lease expiry for an error whose exchange outcome is known.

### After exchange authorization when outcome is uncertain

If an authorized application source exchange fails in a way that cannot prove whether the upstream observed/completed it:

~~~text
DeferUncertainExchange(
  attemptId,
  leaseGeneration,
  retryAvailableAt,
  errorClass,
  errorCode)
~~~

The transition:

- marks the attempt uncertain / uncertain_exchange;
- releases/requeues the same logical collection job when the same lease generation still owns it;
- persists retryAvailableAt;
- clears only that attempt's active endpoint ownership;
- never grants permission to replay the same AttemptId.

It MAY record uncertainty after the lease timestamp has elapsed if recovery has not yet transferred the job to another lease generation. This allows a worker to report the outcome of its already-authorized exchange without falsely claiming current ownership.

If another generation already owns/recovered the job, the stale worker cannot requeue or mutate that newer ownership.

## Retry rule

No FoxData hidden transport retry, hedging, redirect replay or source retry loop is allowed around the upstream War API.

A retry that may perform another FoxData application exchange is always:

~~~text
new AttemptId
new exchange authorization
new audited application exchange
~~~

The old authorized AttemptId remains permanently non-replayable.

SocketsHttpHandler implementation-internal connection recovery is explicitly addressed by ADR-0015 and does not grant application code permission for another SendAsync.

## Eligibility

Next fetch eligibility is at least:

~~~text
max(sourceCacheEligibleAt, configuredTargetAt, retryEligibleAt)
~~~

M2 persists the job-level retry bound as collection_jobs.available_at.

M3 owns source-specific backoff/cache policy and a separate endpoint_poll_state projection. It supplies the resulting retry/eligibility timestamp without bypassing the M2 state machine.

Returned source cache headers win over an earlier local target.

## Successor recovery

A completed Fetch cannot depend on one in-memory continuation to schedule the next poll.

M3 planner/reconciliation derives successor work from durable Fetch + endpoint_poll_state and uses deterministic idempotency such as after-fetch:{FetchId}.

This covers the crash point:

~~~text
raw capture COMMIT
process dies
successor enqueue not yet executed
~~~

The next planner pass repairs the successor without replaying the source exchange.

## Database time

Worker wall clocks do not decide lease ownership.

Where the system must answer "is this lease valid now after any lock wait?", PostgreSQL clock_timestamp() is used after the relevant row lock is held.

Transaction-consistent timestamps may still be used for durable audit fields such as updated_at.

## Measurement before production cadence

Before freezing collection-profile@1, run a 48–72 hour probe on a bounded subset and capture:

- Cache-Control/Expires/Date/Age behavior;
- ETags;
- 200/304 ratio;
- payload sizes;
- compression ratio;
- source change frequency;
- map version behavior;
- latency;
- errors.

Initial conservative target candidates:

- war: 60 seconds;
- maps list: 5 minutes;
- war report: 60 seconds per active region;
- dynamic map: 60 seconds per active region;
- static: first sight plus long conditional revalidation, e.g. 6 hours.

These are hypotheses, not permanent guarantees. A faster profile requires measured upstream/cache behavior and a documented operational reason.

## Unknown commit outcome

A lost database connection during COMMIT is an unknown outcome, not proof of rollback.

Durable operations use stable operation IDs or deterministic uniqueness keys so recovery checks observed state before retrying.

For raw capture, AttemptId is the reconciliation key: an existing Fetch means the capture committed and the source exchange MUST NOT be repeated.

For M3 successor planning, last_processed_fetch_id plus a deterministic successor idempotency key is the reconciliation boundary.

## Crash points

Correctness tests MUST cover failures after:

- job claim COMMIT;
- BeginAttempt COMMIT;
- fence COMMIT;
- exchange-authorization COMMIT before a request;
- source response headers;
- source response received before raw capture;
- raw-capture COMMIT with client-side ambiguity;
- raw capture before source parse;
- source parse before parse-run COMMIT;
- parse-run COMMIT before successor planning;
- successor planning COMMIT with client-side ambiguity;
- semantic/canonical processing COMMIT with client-side ambiguity;
- external outbox effect before completion record.

Recovery cannot depend on graceful shutdown.
