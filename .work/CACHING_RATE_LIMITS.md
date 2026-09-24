# Caching and rate limits

## Upstream collection

Honor official cache headers and ETag/If-None-Match.

Never poll earlier than source cache eligibility merely because a local cadence is shorter.

M3 uses a versioned War API cache policy and separates:

- body-bearing reusable representation;
- latest successful validation;
- latest current Fetch;
- local target cadence;
- retry/backoff eligibility.

A 304 creates a new validation Fetch without a duplicate Payload and references the body-bearing representation Fetch.

Weak ETags are valid validators and are preserved.

Do not synthesize upstream ETags from FoxData payload hashes.

### Freshness calculation

For successful reusable source representations, prefer explicit RFC 9111 freshness:

1. s-maxage when applicable and valid;
2. otherwise max-age;
3. otherwise Expires with usable Date semantics;
4. otherwise no explicit source freshness guarantee.

Do not invent heuristic freshness from Last-Modified in M3.

Valid Date/Age SHOULD be included when calculating current age so FoxData does not revalidate earlier than the source-declared lifetime.

no-cache requires revalidation before reuse.

no-store does not delete immutable FoxData audit evidence, but the response is not promoted into reusable HTTP cache/validator state.

Malformed/conflicting cache directives fail conservative: they never justify polling earlier than a clearly valid source bound, nor do they create an invented long freshness period.

## Upstream retry timing

Retry-After, when valid, is a lower bound and may be HTTP-date or delay-seconds.

HTTP failure backoff is durable successor scheduling, not an in-process retry.

Transport exceptions after authorization use the M2 uncertain-exchange deferral/recovery path and a new AttemptId for any later send.

## Burst control

Source cadence is phase-spread deterministically by stable endpoint identity.

Do not schedule all discovered region endpoints at the same minute/second boundary.

Bound Worker concurrency and per-server HTTP connections independently from cadence.

M4 measures actual cache lifetime, 200/304 ratio and safe cadence before a faster collection profile is accepted.

The local target cadence is owned by a resolved collection policy.

Runtime policy selection is:

~~~text
bootstrap | recommended | custom
~~~

`bootstrap` preserves the M3 target values and remains the default.
`recommended` selects the optional M4-measured `collection-profile@1`.
`custom` lets the operator choose per-capability target cadence/discovery
windows and receives a deterministic policy identity derived from those values.

None of these modes can override a later valid source-cache or Retry-After
eligibility bound.

The mandatory effective scheduling envelope is:

~~~text
effective next request =
  max(
    configured target cadence,
    source cache eligibility,
    Retry-After / durable backoff eligibility
  )
~~~

Outbound transport additionally enforces hard spacing before the HTTP
exchange: at least 400 ms between sends to the same upstream host and at least
150 ms between War API sends globally. These values are FoxData
defence-in-depth ceilings, not claimed Official War API published numeric
limits. They do not authorize polling faster than source cache eligibility,
Retry-After or durable backoff.

Custom cadence outside the M4-measured 15/30/60/120-second dynamic/report
comparison range is permitted, but FoxData MUST describe it as unmeasured by
M4 rather than as a validated recommendation.

The M4 live CI watchdog treats the first observed 429 as a stop signal rather
than attempting to discover the upstream limit.

## Public GET caching

Current mutable resources:

- short explicit freshness;
- ETag;
- conditional GET;
- optional stale-while-revalidate only when documented semantics remain honest.

Sealed immutable historical/export resources:

- long cache lifetime;
- immutable directive when bytes/revision are immutable;
- content hash where useful.

ETags SHOULD derive from representation revision/content rather than wall-clock generation time.

## Rate limiting

Limits protect upstream, database and public service capacity.

Partition primarily by:

- authenticated application/API key;
- anonymous IP fallback;
- operation cost class.

Separate budgets MAY exist for:

- normal reads;
- expensive historical ranges;
- SSE connections;
- export creation;
- webhook management.

429 responses MUST include Retry-After where retry timing is known.

Do not claim draft RateLimit headers as an RFC. If exposed, document their exact draft semantics and treat them as additive convenience metadata.

## Query bounds

Every potentially expensive endpoint defines:

- default page size;
- maximum page size;
- maximum historical range when appropriate;
- maximum export scope;
- timeout/cost budget.

Pagination is cursor-based for mutable/unbounded histories.

## Source protection

Public demand must not trigger synchronous upstream fetch fan-out.

Public API reads canonical/local state. Upstream polling is centrally scheduled, phase-spread and deduplicated.
