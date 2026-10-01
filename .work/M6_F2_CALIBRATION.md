# M6-F2 map-quality calibration record

Status: implementation calibration record for `warapi-map-quality@2`.
Reviewed: 2026-10-01.
Predecessor: M6-F1 golden fixture and deterministic feature layer.
Runtime activation: deferred to M6-G versioned reprocessing.

## Evidence used

### Historical/source-specific fixtures

F2 does not modify `.work/fixtures/m6-map-quality/catalog.json` or any F1 fixture
bytes. Its synthetic controls live in a separate
`m6-map-quality-calibration-fixtures@1` catalog under `.work/calibration`.
This keeps every F1 artifact hash reproducible while allowing calibration-specific
controls to evolve only through a new calibration-corpus version.

- warapi#92: exact upstream dynamic-map JSON from 2021-10-11 14:18 GMT;
- warapi#120: restart false-state symptom represented by a labelled synthetic/minimized fixture because upstream published no JSON;
- warapi#137: exact iconType 97/viewDirection occurrence inside a labelled synthetic wrapper;
- warapi#115: documented duplicate Rocket Target symptom inside a labelled synthetic/minimized representation;
- warapi#77: reported static version/lastUpdated metadata plus synthetic body context;
- explicit healthy, recovery, neutral, taxonomy-open and progression controls.

### Retained M4 evidence

Committed projection:
`.work/calibration/m6-map-quality/m4-calibration-evidence@1.json`

Source:
- campaign `m4-ci-35604611891`;
- final offline reanalysis workflow `35975684583`;
- artifact `10798460093 / m4-f-final-report-35975684583`;
- artifact digest `sha256:91411ee4ca339f2f56a945d9f8d4e235c598154d191c22b1a69ecc8a3b606e9e`;
- measurement-summary SHA-256 `2cf939f8231cfe1a94c83dca7f7423d27f87dc9b6b4a215164abf683df2d1239`.

Dynamic-map-state retained summary:

| measurement | value |
| --- | ---: |
| endpoints | 53 |
| Fetches | 156,227 |
| body-bearing representations | 7,637 |
| representation changes | 7,077 |
| source-version regressions | 53 |
| endpoints with version regression | 53 / 53 |
| source-version missing-version count | 4,685 |
| endpoints with version gaps | 52 / 53 |
| source-lastUpdated regressions | 0 |
| structural-fingerprint changes | 0 |
| parser unknown-code observations | 2,505 |
| endpoints with unknown codes | 14 |

Consequences:
- source-version regression is informational by itself;
- source-version gaps are informational by themselves;
- unknown taxonomy values are non-blocking by themselves;
- source-lastUpdated regression is suspect, not quarantine.

The retained M4 summary does not contain per-representation item/team/icon composition.
It therefore cannot supply population quantiles for NONE share, item-count collapse or
icon-composition collapse. Composition thresholds below are conservative regression
guardrails validated against historical/minimized fixtures plus negative controls;
they are not universal distribution estimates.

## Policy@2 calibrated rules

The original eight policy@1 structural rules remain unchanged, including their
rule/configuration versions and historical input-metrics shape.

| rule | effect | parameters |
| --- | --- | --- |
| `source-version.regression` | informational | delta < 0 |
| `source-version.gap` | informational | >= 1 missing version |
| `source-last-updated.regression` | suspect | delta < 0 ms |
| `representation.near-empty` | suspect | baseline occurrences >= 8 and ratio <= 0.20 |
| `representation.mass-disappearance` | suspect | baseline items >= 12 and ratio <= 0.50 |
| `representation.duplicate-occurrence` | informational | excess >= 1 |
| `ownership.restart-collapse` | quarantined | composite below |

Restart-collapse requires all of:
- baseline item count >= 12;
- baseline owned-team share >= 0.50;
- current NONE share >= 0.90;
- NONE-share increase >= 0.50;
- current owned-team share <= 0.10;
- owned-team-share drop >= 0.50.

It also requires at least one corroborating signal:
- source version regression/reset; or
- item-count ratio <= 0.80; or
- distinct raw iconType ratio <= 0.75.

A high NONE share alone cannot quarantine a representation.

## False-positive controls

The fail-closed calibration set includes healthy baseline/recovery, no-baseline
all-NONE war start, stable neutral continuation, isolated version regression/gap,
unknown taxonomy on healthy shape, duplicate occurrences, static change and severe
count collapse without restart ownership signature.

The runner fails when the expected decision differs, a required rule does not hit,
or a forbidden rule hits. Each report retains candidate/baseline hashes, structural
fingerprints, features, findings, rule/config identities and deterministic metrics.
CI runs calibration twice and requires byte-identical output.

## Runtime activation boundary

F2 publishes and validates `warapi-map-quality@2`, but
`WarApiVersions.MapQualityPolicy` remains pinned to `warapi-map-quality@1`.

Switching now would make every historical @1 snapshot appear to be pending @2 work
to the existing E3 recovery path and would violate ordered chronology. Generalized
versioned reprocessing belongs to M6-G. M6-G must implement ordered policy/taxonomy
reprocessing before runtime selection moves to @2.

## Exit criteria

F2 is accepted when policy@1 remains unchanged, policy@2 validates, calibration has
zero mismatches, #92/#120 quarantine via the composite rule, neutral/unknown controls
do not, version regression/gap remain non-blocking alone, disappearance remains
suspect, policy@2 quarantine cannot become an accepted baseline, the current @1
runtime path keeps structural finding compatibility, and all repository gates pass.

Next: M6-G versioned coverage/recovery/reprocessing and controlled policy@2 activation.
