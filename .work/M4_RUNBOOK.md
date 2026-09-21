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

The repository contains an automatic one-shot hosted-runner workflow:

~~~text
.github/workflows/m4-live-campaign.yml
~~~

Pull requests only validate the workflow/action definition and MUST issue zero
War API requests. The live campaign starts only after the one-shot marker
.work/M4_E_V2_START is merged to main. There is no manual approval or
workflow_dispatch step in the M4-E v2 path.

The push first runs a three-job offline checkpoint rehearsal on separate
GitHub-hosted runners. It performs create -> persist -> fresh-runner restore ->
mutate -> persist -> second fresh-runner restore and also proves that corrupted
or SHA-less checkpoints fail closed. This rehearsal issues zero War API
requests.

Only after the rehearsal succeeds does the workflow run the six-minute Live-1
canary. The 48-hour campaign starts automatically only when both gates succeed.

The hosted-runner preset records 48 hours of active Worker runtime as 12
sequential four-hour jobs:

~~~text
segment 01       4 h   Live-1 baseline
segments 02-03   8 h   Live-1 bounded probe
segments 04-05   8 h   Live-1 + Live-2, probe disabled
segments 06-12  28 h   Live-1 + Live-2 + Live-3, probe disabled
~~~

Four-hour segments remain below the hosted-runner job execution ceiling while
leaving time for restore/build/analyze/checkpoint work. Every segment checks out
the exact campaign SHA.

The Worker outbound admission gate enforces a safety envelope before durable
HTTP exchange authorization:

~~~text
same upstream host: >= 400 ms between sends (<= 2.5 req/s)
all War API hosts:  >= 150 ms between sends (<= ~6.67 req/s)
~~~

These are hard safety ceilings, not target polling rates. Normal collection is
slower because capability cadence, source cache eligibility and Retry-After are
also lower bounds.

The live loop additionally checks durable Fetch evidence every 10 seconds and
stops the Worker immediately when it observes:

- any 401/403;
- any 429;
- any body read/limit error;
- more than 390 captured requests in the preceding 60 seconds;
- at least five 5xx responses when they are also at least 5% of the preceding
  five-minute Fetch window.

The post-segment guard remains a second line of defence and also checks root
404s, uncertain exchanges, parse failures and disk pressure.

PostgreSQL checkpoint handling is centralized in
.github/scripts/m4-checkpoint.sh. pg_dump writes to a host-side temporary file;
validation copies that file into the container and runs pg_restore --list on
the file directly, so validation never depends on an early-closing shell pipe.
The dump is promoted atomically only after structural validation and receives a
SHA-256 sidecar.

The authoritative segment checkpoint is persisted immediately after collection
and before per-segment analysis or safety-report generation. It is written to
an exact-key GitHub Actions cache and also uploaded as a short-retention
recovery artifact. Therefore an analyzer/reporting failure cannot discard an
already completed collection segment.

A rerun of the same failed GitHub job first looks for the exact checkpoint of
that segment. If the cache exists, or the recovery artifact exists, collection
is skipped and the job resumes from the persisted database state. Only an
unexpected failure before authoritative checkpoint creation may require
repeating that segment.

The final job requires the exact 12-segment sequence and at least 172,800
recorded active Worker seconds. Wall-clock runner provisioning/restore gaps do
not count toward the 48-hour active-observation requirement.

The hosted-runner observer label is github-actions-hosted-variable. Because
GitHub-hosted jobs are not guaranteed to originate from one stable network
location, latency results retain that limitation.

## 4. Preflight

Required repository gates:

~~~text
dotnet restore FoxData.slnx --locked-mode
dotnet format FoxData.slnx --verify-no-changes --no-restore
dotnet build FoxData.slnx -c Release --no-restore
dotnet test FoxData.slnx -c Release --no-build
~~~

Before enabling source collection:

- require the multi-runner checkpoint rehearsal to be green;
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

Target window: 4 active hours.

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

Target window: 8 active hours.

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

Target window: 8 active hours after the probe phase.

Enable Live-2 without changing the global bootstrap profile and disable the
probe before expanding the shard set:

~~~text
WarApi__EnabledShards__0=live-1
WarApi__EnabledShards__1=live-2
~~~

~~~text
WarApi__MeasurementProbe__Enabled=false
~~~

Do not combine first-time shard expansion with elevated probe cadence.

## 8. Phase 4 — add Live-3

Target window: 28 active hours after Live-2 expansion.

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

Stop expansion and disable ingestion when any hard upstream-protection
condition becomes credible. In the automated M4-E v2 path, the first observed
401/403 or 429 is sufficient to stop the current segment.

- any 401/403 from live source endpoints;
- root endpoint 404;
- any 429;
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
