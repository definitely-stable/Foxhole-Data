# M6-G exit gate

Status: implementation candidate.

This gate closes M6-G only when the source-neutral recovery chain is both
convergent and honest about unfinished work.

## Completion predicate

Quality recovery is quiescent only when:

```
ProgressCount == 0
AND OutstandingCount == 0
```

where:

```
OutstandingCount = Deferred + VersionBlocked
```

A pass with zero progress and any deferred/version-blocked work is explicitly
NOT quiescent.

The executable contract is
`WarApiMapQualityRecoveryResult.IsQuiescent`.

## Evidence matrix

| Requirement | Executable evidence |
| --- | --- |
| recovery uses no new upstream Fetch | `RawDurableWarCaptureRecoversThroughM5WithoutAnotherFetch`, `CrossWar304QualityRecoveryReusesSnapshotAndResetsBaseline`, `PolicyV2OrderedRebuildResumesAndPreservesVersionIsolation`, `LaterQualityCandidateDefersUntilEarlierGapRecovers` |
| 304 creates no duplicate snapshot/occurrence state | `Map304ReusesPriorRepresentationAndSingleLocalParseRepair`, `CrossWar304QualityRecoveryReusesSnapshotAndResetsBaseline` |
| quality recovery converges | G5/G6/G7 recovery scenario, G8-C ordered policy@2 rebuild, G9 transaction retry |
| deferred work stays visible/retryable | cross-war candidate before M5 continuity and direct later-before-earlier G9 candidate |
| process restart resumes durable work | G8-C partial policy@2 rebuild recreates the coordinator before continuation |
| quality transaction interruption leaves no partial graph | G9 PostgreSQL fault-injection test |
| zero-progress with outstanding work is not quiescent | `M6RecoveryExitGateTests.QualityRecoveryQuiescenceRequiresZeroProgressAndZeroOutstanding` plus the cross-war deferred assertion |
| live quality version is policy@2 while @1 stays replayable | G8-D SourceTests and G8B/G8C explicit historical target tests |
| M5 canonical/recovery behavior remains compatible | the complete Integration and Recovery projects run in the same CI gate |

## Source-state preservation

Recovery/reprocessing tests freeze and compare durable source-state counts where
appropriate:

- Fetch;
- Payload;
- SourceParseRun;
- NormalizationRun;
- MapSnapshot;
- map item occurrences;
- map text occurrences.

M6 quality recovery must not manufacture source evidence.

## 304 rules

Both same-war and cross-war 304 quality bindings reuse the exact prior
MapSnapshot. Cross-war binding may change only the target WarRegion/quality
identity after M5 proves the validation-boundary membership.

No 304 path creates a new map payload, parse run, normalization run, snapshot
or occurrence row.

## Deferred semantics

Deferred work is durable outstanding work, not success.

The background worker may delay before retrying a no-progress deferred pass,
but it must not classify that state as completion.

Version-blocked work is likewise outstanding and requires explicit
versioned-rebuild handling.

## Exit condition

M6-G is complete only when the full repository gate is green with:

- live `warapi-map-quality@2`;
- G1-G9 recovery tests green;
- the explicit quiescence contract green;
- Contracts green;
- Dependency Review green;
- Docker smoke green.

After this gate, M6-H owns the stronger milestone-level delete/rebuild,
bidirectional-provenance and semantic-equality proof.
