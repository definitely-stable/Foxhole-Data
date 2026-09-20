# Time semantics

Foxhole-Data MUST not collapse different clocks into a single timestamp.

Keep separate:

- requestedAt — local request start;
- retrievedAt — local completed source retrieval time;
- observedAt — accepted observation boundary used by canonical history;
- sourceUpdatedAt — source-provided map lastUpdated when available;
- recordedAt — local durable database recording time;
- sourceEventAt — only when the source explicitly supplies an event time.

Canonical API timestamps use UTC RFC 3339 strings.

Compatibility API preserves documented source epoch-millisecond fields.

## Observed changes

If an objective is observed as WARDENS at t1 and COLONIALS at t2, the safe statement is:

owner changed in the interval (t1, t2]

The canonical reconstruction boundary may be t2.

Foxhole-Data MUST NOT fabricate a timestamp between the polls.

## War-relative time

dayOfWar from region reports is source data and may be useful, but historical issue 81 proves it is not sufficient as the canonical global war clock.

Derived war-relative time SHOULD use accepted conquest start anchors and a versioned calculation.

If a time anchor is corrected, derived buckets can be recomputed; immutable source observations are not rewritten.
