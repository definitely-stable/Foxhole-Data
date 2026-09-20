# Performance and capacity

Do not lock polling, retention or infrastructure decisions from intuition.

## 48–72 hour source measurement

Capture:

- active region count per shard;
- cache lifetime distribution;
- Date/Age/Retry-After behavior;
- ETag 200/304 ratio;
- payload p50/p95/p99;
- encoded and decoded size where relevant;
- semantic change frequency;
- source latency/error rates;
- DB write/index growth;
- source parsing/fingerprint CPU;
- normalization CPU;
- quality anomaly rate.

## Poll cardinality awareness

General high-frequency per-map request volume:

~~~text
activeMaps
* highFrequencyEndpointFamilies
* pollsPerDay
* shards
~~~

At 30 active regions, two per-region endpoints and 60-second cadence:

~~~text
30 * 2 * 1440 = 86,400 per-region requests per shard per day
~~~

Across three live shards this is approximately 259,200 per day before war/maps/static requests.

A September 2026 Live-1 point-in-time observation returned 53 map names; research/M3_WAR_API_HTTP_2026-09.md records the corresponding higher cardinality example. This observation is not a permanent capacity assumption.

A 3-second source update capability does not mean the platform should blindly poll every region every 3 seconds.

## Burst shape

Average request rate is not sufficient capacity planning.

M3 must also measure and control burst shape:

- initial map discovery;
- worker restart;
- shard re-enable;
- cache expiry alignment;
- many endpoints with the same cadence.

Use deterministic endpoint phase spreading and bounded Worker/HTTP concurrency so a restart does not collapse all region requests onto the same second.

M4 should record both average requests/second and short-window peak request rate.

## API budgets

Measure:

- p50/p95/p99 latency;
- response bytes;
- cache hit ratio;
- rows scanned;
- historical query duration;
- SSE concurrent connections;
- export throughput;
- webhook throughput.

## Storage

Separate:

- immutable changed raw representations;
- transport audit/304 records;
- source parse runs/fingerprints;
- canonical observations;
- state intervals;
- changes;
- exports.

If unchanged transport audit volume becomes material, compact old transport records into columnar cold storage while preserving reproducible coverage.

Do not implement compaction before measurements justify it.
