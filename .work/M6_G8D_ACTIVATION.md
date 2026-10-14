# M6-G8D — policy@2 activation record

Status: implementation candidate.

## Preconditions

The activation is allowed only after the preceding G8 slices are green:

- G8-A: production and offline calibration use the same
  `WarApiMapQualityPolicyEvaluator`;
- G8-B: live policy selection is separated from explicit version-targeted
  recovery;
- G8-C: an ordered policy@2 rebuild over durable PostgreSQL evidence converges
  after a simulated process restart with zero new source-state writes.

The G8-C proof includes accepted, suspect and quarantined decisions, same-war
304, cross-war 304, WarRegion baseline reset, version-isolated baseline
selection and idempotent replay.

## Runtime consumer audit

At activation time there is no product-facing Current War, Timeline or Replay
reader in `src` that directly consumes `runtime.map_observations`.

The table is currently written by the M6 quality transaction and read by the
M6 quality ordering/baseline machinery. Those ordering reads are already
scoped by both `taxonomy_version` and `quality_policy_version`.

Therefore activating policy@2 cannot make an existing product projection mix
@1 and @2 observations. Any future M7+ observation reader MUST select an
explicit active taxonomy/policy identity instead of treating all rows as one
logical stream.

## Activation

`WarApiVersions.MapQualityPolicy` moves from
`warapi-map-quality@1` to `warapi-map-quality@2`.

`warapi-map-quality@1` remains registered, immutable and explicitly
replayable through `WarApiMapQualityTarget.ForPolicyVersion(...)`.

No @1 QualityRun or MapObservation is rewritten or deleted.

No schema migration is required.

## Recovery behavior after activation

Ordinary Worker quality evaluation and quality-gap recovery now target @2.

Historical or diagnostic @1 rebuilds must request the @1 target explicitly.

The activation does not alter exact 200/304 provenance, WarRegion context
resolution, chronology ordering or the atomic quality transaction.

## Rollback property

Because both policies remain immutable and version-targeted, reverting the live
selector to @1 does not require deleting @2 results. It changes only which
policy new ordinary Worker/recovery work selects.

## Success gate

The activation PR must keep:

- F1 feature lab green;
- F2 calibration green;
- Unit, Integration, Recovery, Source and Contract tests green;
- Docker smoke green;
- Contracts workflow green;
- Dependency Review green.

Runtime selection tests must assert @2 is active while explicit @1 recovery
continues to converge independently.
