# M4 collection policy result

Status: candidate pending final offline publication reanalysis.

## 1. Decision

FoxData does not impose one universal polling cadence on every deployment.

M4 separates four concepts:

1. **Source measurements** — observed Official War API behaviour and the scope of that evidence.
2. **RecommendedCollectionProfile** — an optional FoxData-published preset derived from those measurements.
3. **CollectionPolicy** — the operator-selected runtime policy: `bootstrap`, `recommended`, or `custom`.
4. **SafetyEnvelope** — mandatory source-protection rules applied to every policy and not bypassable by custom cadence.

The current bootstrap preset remains the default so upgrading FoxData does not silently alter source traffic.

## 2. M4 campaign scope

Campaign id: `m4-ci-35604611891`.

Observed active Worker time: approximately 48.169 hours across 12 four-hour segments.

The bounded high-resolution probe covered:

- shard: `live-1`;
- three deterministic map regions, not three worlds;
- capabilities: `dynamic-map-state` and `region-war-report`;
- probe target: 15 seconds;
- active probe duration: 8 hours;
- counterfactual cadences: 15, 30, 60 and 120 seconds.

Dev was excluded from the campaign. Live-2 and Live-3 root endpoints returned HTTP 503 during the measured expansion phases and therefore did not provide an active-war map fleet comparable with Live-1.

The measured trade-offs below are evidence for the observed cohort, not a universal completeness guarantee for every future war or region.

## 3. Measured cadence trade-off

Across the deterministic three-region Live-1 probe:

| Capability | Candidate cadence | Observed representation episodes retained | Simulated requests | Worst endpoint p95 additional observation delay |
| --- | ---: | ---: | ---: | ---: |
| dynamic-map-state | 15 s | 26 / 26 | 5,570 | 0 s |
| dynamic-map-state | 30 s | 26 / 26 | 2,789 | 15.49 s |
| dynamic-map-state | 60 s | 26 / 26 | 1,395 | 46.85 s |
| dynamic-map-state | 120 s | 26 / 26 | 699 | 108.40 s |
| region-war-report | 15 s | 73 / 73 | 5,570 | 0 s |
| region-war-report | 30 s | 73 / 73 | 2,789 | 15.81 s |
| region-war-report | 60 s | 73 / 73 | 1,395 | 46.94 s |
| region-war-report | 120 s | 73 / 73 | 699 | 108.90 s |

Interpretation:

- 30 s used roughly half as many simulated probe requests as 15 s while retaining every observed representation episode in this cohort;
- 60 s halved requests again but increased the worst endpoint p95 observation delay to about 47 s;
- 120 s reduced request count to about one eighth of 15 s but pushed the worst endpoint p95 delay to about 109 s;
- `sourceVersionGap` is an anomaly/evidence signal, not a direct count of proven missed externally observable states.

## 4. Optional FoxData recommended profile

`collection-profile@1` is the balanced FoxData recommendation, not a mandatory runtime policy.

| Capability | Recommended target | Discovery window | Rationale |
| --- | ---: | ---: | --- |
| runtime-war-state | 60 s | 60 s | No comparative high-resolution cohort; retain conservative measured baseline. |
| active-map-list | 300 s | 300 s | Map-list representation was effectively stable during the campaign; avoid unnecessary source traffic. |
| region-war-report | 30 s | 30 s | Measured 2x request reduction vs 15 s with ~16 s worst endpoint p95 extra observation delay and no observed episode loss in the probe cohort. |
| static-map-state | 6 h | 5 min | Static data is not declared immutable; retain long conditional revalidation and spread discovery. |
| dynamic-map-state | 30 s | 30 s | Same balanced trade-off as region reports for the measured three-region cohort. |

Executor concurrency remains `1`. The measured required serial service load was approximately 0.157, leaving about 84.3% modeled serial headroom.

## 5. Runtime CollectionPolicy

Runtime selection:

~~~text
WarApi:Collection:Preset = bootstrap | recommended | custom
~~~

Default:

~~~text
bootstrap
~~~

Recommended opt-in:

~~~text
WarApi__Collection__Preset=recommended
~~~

Custom example:

~~~text
WarApi__Collection__Preset=custom
WarApi__Collection__Custom__runtime-war-state__TargetCadenceSeconds=60
WarApi__Collection__Custom__active-map-list__TargetCadenceSeconds=300
WarApi__Collection__Custom__region-war-report__TargetCadenceSeconds=45
WarApi__Collection__Custom__static-map-state__TargetCadenceSeconds=21600
WarApi__Collection__Custom__static-map-state__DiscoveryWindowSeconds=300
WarApi__Collection__Custom__dynamic-map-state__TargetCadenceSeconds=45
~~~

When `DiscoveryWindowSeconds` is omitted for a custom capability it defaults to that capability's target cadence.

A custom policy receives a deterministic identity derived from its resolved cadence/discovery values. Changing custom cadence therefore changes the scheduling-policy identity recorded in evidence.

## 6. Mandatory SafetyEnvelope

Every preset and custom policy is constrained by the same safety rules.

For each successor request, effective eligibility is bounded by:

~~~text
effective next request =
  max(
    operator/preset target cadence,
    source Cache-Control / Expires eligibility,
    Retry-After / durable backoff eligibility
  )
~~~

Additionally:

- ETag / If-None-Match is used when a validator is available;
- no in-process retry storm or hedging is allowed;
- the outbound governor enforces at least 400 ms between sends to the same upstream host;
- the outbound governor enforces at least 150 ms between War API sends globally;
- public/client demand never causes synchronous upstream fan-out;
- Dev cannot be enabled implicitly;
- custom cadence outside the measured 15–120 s dynamic/report range is permitted but is explicitly **unmeasured by M4**, not promoted as a FoxData recommendation.

The 150 ms / 400 ms transport spacings are FoxData defence-in-depth ceilings, not claimed Official War API published numeric limits.

## 7. Other M4 observations

The campaign recorded approximately:

- 317,715 Fetch observations;
- 19,911 HTTP 200;
- 296,168 HTTP 304;
- 1,636 HTTP 503;
- zero HTTP 429;
- zero HTTP 401/403;
- no observed same-ETag/different-payload anomaly;
- no observed different-ETag/same-payload anomaly.

Physical PostgreSQL growth over the campaign was approximately 432 MB, extrapolating at the measured workload to roughly 6.46 GB / 30 days and 78.6 GB / year before retention, compaction or lifecycle optimization.

Those projections describe this measurement workload. They are not a storage SLA.

## 8. Publication gate

The first final validation found four Fetch rows without a scheduling decision because the analyzer incorrectly filtered decisions by `decision.CreatedAt` rather than by their causal association with an active Fetch or active successor job.

The raw campaign and s12 checkpoint are retained. The fix is reanalyzed offline against that existing checkpoint; no replacement 48-hour collection is required.
