# M4 — Controlled Measurement Runbook

Status: normative operations procedure.
Milestone: M4 Source Measurement.
Date: 2026-09-20.

This runbook operates the measurement substrate defined in
[M4_SOURCE_MEASUREMENT.md](M4_SOURCE_MEASUREMENT.md).

It does not authorize canonical-state work and it does not bypass the M2/M3
evidence, fencing, cache or retry boundaries.

## 1. Safety invariants

The campaign must preserve all of the following:

- `WarApi:MeasurementProbe:Enabled=false` is the repository default;
- Dev is never part of the live campaign;
- a probe shard must also be enabled in `WarApi:EnabledShards`;
- the probe cohort is deterministic from run id, shard and map identifier;
- no more than three maps per enabled probe shard are selected;
- probe cadence is never below 15 seconds;
- source cache eligibility and Retry-After remain lower bounds;
- the serial M3 application exchange executor remains unchanged unless the
  measured report later proves insufficient capacity;
- raw Fetch/Payload evidence is never rewritten as part of measurement;
- connection strings are supplied through environment/secret configuration,
  never as command-line arguments and never written into artifacts.

## 2. Fixed campaign identity

Choose one stable run id before the first storage snapshot and do not change it
during the campaign.

Example:

~~~text
m4-2026-09-live
~~~

Record the exact repository commit SHA that is deployed for the run.

The output directory should be dedicated to this campaign, for example:

~~~text
artifacts/m4/m4-2026-09-live/
~~~

The `artifacts/` tree is gitignored. Do not commit raw measurement artifacts or
source payloads.

## 3. GitHub Actions campaign

The repository contains a manual workflow:

~~~text
.github/workflows/m4-live-campaign.yml
~~~

It has two modes:

- `smoke` — one two-minute Live-1 baseline segment to verify runner, PostgreSQL,
  migration, Worker, analyzer, checkpoint and safety-guard plumbing;
- `full` — the controlled M4-E campaign.

The full hosted-runner preset records 72 hours of active Worker runtime as
18 sequential four-hour jobs:

~~~text
segments 01-02   8 h   Live-1 baseline
segments 03-04   8 h   Live-1 bounded probe
segments 05-06   8 h   Live-1 + Live-2, probe remains Live-1 only
segments 07-18  48 h   Live-1 + Live-2 + Live-3, probe remains Live-1 only
~~~

Four-hour segments stay below the hosted-runner job execution limit while the
workflow remains far below the workflow-level execution limit. Every job checks
out the same workflow-dispatch SHA.

PostgreSQL is checkpointed with exact-key GitHub Actions caches between jobs.
The preceding checkpoint is deleted only after the next segment passes its
safety guard, so a failed segment leaves the last known-good checkpoint
available. Raw evidence is not committed to git.

The hosted-runner observer label is
`github-actions-hosted-variable`. Because GitHub-hosted jobs are not guaranteed
to originate from one stable network location, source latency and derived
executor-capacity results from this mode must retain that limitation. A
stable-region self-hosted campaign may later be used if latency precision proves
decision-critical.

The per-segment guard stops expansion for:

- any 401/403;
- any 404 on root `war` or `maps`;
- at least three 429 responses in a segment;
- any body read/limit error;
- at least three uncertain exchanges in a segment;
- at least five 5xx responses when they are also at least 5% of segment Fetches;
- less than 1 GiB runner disk after collection.

Parse failures are surfaced as warnings for evidence review rather than silently
converted into a source-health conclusion.

The final job requires the exact 18-segment sequence and at least 259,200
recorded active Worker seconds, generates the complete report, runs
`validate`, uploads the report artifact and fails if publication evidence is
structurally incomplete.

The manual workflow can only be dispatched after the workflow file exists on
the default branch. Run `smoke` first, then `full`.

## 4. Preflight

Required repository gates:

~~~text
dotnet restore FoxData.slnx --locked-mode
dotnet format FoxData.slnx --verify-no-changes --no-restore
dotnet build FoxData.slnx -c Release --no-restore
dotnet test FoxData.slnx -c Release --no-build
~~~

Before enabling source collection:

- apply the exact migration bundle from the deployed commit;
- verify API readiness;
- verify Worker starts with `WarApi__Enabled=false`;
- confirm adequate PostgreSQL disk headroom;
- confirm the non-blocking Live-1 canary is healthy;
- record deployment/observer region only at a coarse, non-secret level.

Take the physical storage baseline:

~~~text
dotnet run --project tools/FoxData.SourceMeasurement -c Release --no-build --   storage   --label before   --output artifacts/m4/m4-2026-09-live
~~~

## 5. Phase 1 — Live-1 baseline

Target window: approximately 6 hours.

Environment:

~~~text
WarApi__Enabled=true
WarApi__EnabledShards__0=live-1
WarApi__EnableDev=false

WarApi__MeasurementProbe__Enabled=false
~~~

Expected local bootstrap targets remain:

~~~text
runtime-war-state  60 s
active-map-list     5 min
region-war-report  60 s
dynamic-map-state  60 s
static-map-state    6 h
~~~

Do not add Live-2/Live-3 and do not enable the probe during this phase.

## 6. Phase 2 — bounded Live-1 probe

Target window: approximately 6 hours.

Keep Live-1 enabled and activate only the deterministic probe cohort:

~~~text
WarApi__Enabled=true
WarApi__EnabledShards__0=live-1
WarApi__EnableDev=false

WarApi__MeasurementProbe__Enabled=true
WarApi__MeasurementProbe__RunId=m4-2026-09-live
WarApi__MeasurementProbe__EnabledShards__0=live-1
WarApi__MeasurementProbe__MaxMapsPerShard=3
WarApi__MeasurementProbe__TargetCadenceSeconds=15
~~~

Only selected `dynamic-map-state` and `region-war-report` endpoints receive
the faster local target. Static maps, root endpoints and unselected maps retain
the bootstrap profile.

The scheduling-decision ledger must show the effective cadence and whether each
Fetch decision was probe-selected. A probe-selected 15-second local target may
still create a successor later than 15 seconds when source cache or Retry-After
requires it.

## 7. Phase 3 — add Live-2

Target window: approximately hour 12 through hour 24.

Enable Live-2 without changing the global bootstrap profile:

~~~text
WarApi__EnabledShards__0=live-1
WarApi__EnabledShards__1=live-2
~~~

The recommended default is to keep the high-resolution probe limited to Live-1
while Live-2 establishes baseline behaviour:

~~~text
WarApi__MeasurementProbe__EnabledShards__0=live-1
~~~

Do not add Live-2 to the probe cohort in the same deployment that first enables
the shard. Separate shard expansion from cadence expansion.

## 8. Phase 4 — add Live-3

Target window: approximately hour 24 through hour 72.

Enable the final live shard:

~~~text
WarApi__EnabledShards__0=live-1
WarApi__EnabledShards__1=live-2
WarApi__EnabledShards__2=live-3
~~~

Keep Dev disabled:

~~~text
WarApi__EnableDev=false
~~~

Do not increase executor concurrency during the campaign unless a separate,
reviewed change is justified by measured queue/scheduling lag.

## 9. Stop / hold conditions

Stop expansion and normally disable ingestion when any of these becomes
credible and persistent:

- 401/403 from live source endpoints;
- root endpoint 404;
- sustained 429;
- sustained 5xx burst;
- unexpected response-header/body-limit failures;
- material uncertain-exchange burst;
- unbounded queue or scheduling lag;
- database disk pressure;
- parser/fingerprint drift that makes current parsing unsafe;
- any evidence/fencing invariant failure.

Rollback:

~~~text
WarApi__Enabled=false
~~~

A rollback must not delete or rewrite:

- source registry rows;
- collection jobs or attempts;
- Fetch/Payload evidence;
- parse runs;
- scheduling decisions;
- poll state.

## 10. End-of-run freeze

Record the exact analysis window as offset-aware timestamps:

~~~text
START=<ISO-8601 inclusive>
END=<ISO-8601 exclusive>
SHA=<exact deployed commit>
RUN_ID=m4-2026-09-live
REGION=<coarse non-secret region>
~~~

Take the final physical storage snapshot:

~~~text
dotnet run --project tools/FoxData.SourceMeasurement -c Release --no-build --   storage   --label after   --output artifacts/m4/m4-2026-09-live
~~~

Then generate the reproducible report:

~~~text
dotnet run --project tools/FoxData.SourceMeasurement -c Release --no-build --   analyze   --run-id "$RUN_ID"   --start "$START"   --end "$END"   --output artifacts/m4/m4-2026-09-live   --observer-region "$REGION"   --repository-sha "$SHA"   --profile-version warapi-bootstrap-profile@1   --probe-shards live-1   --probe-max-maps 3   --probe-target-seconds 15
~~~

Validate the frozen artifact set before M4-F publication work:

~~~text
dotnet run --project tools/FoxData.SourceMeasurement -c Release --no-build --   validate   --output artifacts/m4/m4-2026-09-live
~~~

Expected outputs:

~~~text
measurement-manifest.json
measurement-summary.json
measurement-report.md
measurement-validation.json
storage-before.json
storage-after.json
~~~

## 11. Report acceptance checks

Before using the report to publish `collection-profile@1`, verify:

- the selected window contains official War API Fetches;
- scheduling-decision coverage is explained and boundary-unattributed Fetches
  are not silently classified;
- probe-attributed Fetches exist for the intended shard and endpoint families;
- cache/retry successor extension is represented separately from local cadence;
- 200/304 and validator metrics use actually observed traffic;
- downsampling is based only on attributable probe traffic and does not pretend
  to simulate counterfactual HTTP validator history;
- map version gaps/regressions and lastUpdated regressions are reported;
- physical PostgreSQL growth includes the M4 scheduling ledger itself;
- storage projections state their measurement duration and limitations;
- no secret, connection string or raw payload is present in generated outputs.

## 12. Publication gate

M4-F may begin only after at least 48 hours of controlled live evidence are
frozen and analyzed.

The publication change must contain:

- the research report or a durable reference to the measured artifact set;
- `collection-profile@1`;
- the executor-concurrency decision;
- the ADR-0010 retention/supersession decision;
- explicit remaining limitations;
- M4 completion record;
- `IMPLEMENTATION_PLAN.md` updated so M5 is next.

Until that publication is reviewed and merged, the Worker continues using the
bootstrap profile.
