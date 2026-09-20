# Performance and capacity

Do not lock polling, retention or infrastructure decisions from intuition.

## 48–72 hour source measurement

Capture:

- active region count per shard;
- cache lifetime distribution;
- ETag 200/304 ratio;
- payload p50/p95/p99;
- compressed size;
- semantic change frequency;
- source latency/error rates;
- DB write/index growth;
- normalization CPU;
- quality anomaly rate.

## Poll cardinality awareness

At 30 active regions, two per-region endpoints and 60-second cadence:

30 * 2 * 1440 = 86,400 per-region requests per shard per day.

Across three live shards this is approximately 259,200 per day before war/maps/static requests.

A 3-second source update capability does not mean the platform should blindly poll every region every 3 seconds.

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
- canonical observations;
- state intervals;
- changes;
- exports.

If unchanged transport audit volume becomes material, compact old transport records into columnar cold storage while preserving reproducible coverage.

Do not implement compaction before measurements justify it.
