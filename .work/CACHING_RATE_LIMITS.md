# Caching and rate limits

## Upstream collection

Honor official cache headers and ETag/If-None-Match.

Never poll earlier than source cache eligibility merely because a local cadence is shorter.

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

Public API reads canonical/local state. Upstream polling is centrally scheduled and deduplicated.
