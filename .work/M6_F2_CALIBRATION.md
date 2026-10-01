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

## Verified offline calibration outcome

A successful CI replay over the isolated calibration corpus produced:

- cases: 20;
- matched: 20;
- mismatches: 0;
- accepted: 13;
- suspect: 3;
- quarantined: 4;
- deterministic report SHA-256:
  `b1aa86ce7c49d0916dc863fb4e14c5ba02599ce8e1cc6943181559cd55df1614`.

Representative results:

| case | decisive features | result / rule hits |
| --- | --- | --- |
| exact warapi#92 | item ratio 0.75; NONE 1.0 vs 0.0833; owned 0 vs 0.9167; distinct-icon ratio 0.5714; version delta -7 | quarantined; version regression + restart-collapse; all three corroborators true |
| minimized warapi#120 | item ratio 0.25; NONE 1.0 vs 0.0833; distinct-icon ratio 0.2857; version delta -7 | quarantined; version regression + mass-disappearance + restart-collapse |
| stable neutral continuation | item ratio 1.0; NONE 1.0 -> 1.0; owned 0 -> 0; version +1 | accepted; no findings |
| isolated version regression | item ratio 1.0; ownership/composition unchanged; version delta -4 | accepted; informational version-regression only |
| near-empty control | 2 vs 24 items; ratio 0.0833; ownership remains fully faction-owned | suspect; near-empty + mass-disappearance, no restart-collapse |

The exact #92 source also carries five observations of raw flag bit 0x08 that
taxonomy@1 treats as unknown. That produces only an informational taxonomy finding
and is not a restart-corruption predicate. This is deliberate evidence that the
quarantine depends on the composite ownership/corroboration rule rather than an
unrelated unknown-code diagnostic.

The complete report includes all feature vectors, hashes, rule/config identities,
input metrics, decisions and false-positive notes and is persisted by CI as
`m6-f2-calibration-<run-id>`.

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
