# Quality and coverage

Raw collection success is not the same thing as canonical acceptance.

Pipeline:

~~~text
raw durable
   |
versioned source parse
   |
versioned normalization
   |
versioned taxonomy
   |
quality gate
   +-- accepted
   +-- suspect
   +-- quarantined
   |
canonical reconcile only when policy allows
~~~

A structurally unusable representation is recorded as a parser/normalizer rejection. `schema_rejected` may be exposed later as a reporting category, but M6 does not invent a QualityRun for a normalized map snapshot that does not exist.

For M6 maps:

- only `accepted` decisions create `runtime.map_observations` under quality policy v1;
- `suspect` and `quarantined` decisions persist quality evidence but do not replace the accepted baseline;
- all source occurrences remain recoverable from the exact representation;
- item-scoped findings never silently delete individual source occurrences.

## Initial rule families

- required field missing;
- incompatible source type;
- unknown but preservable code;
- regionId unexpected change;
- map version regression;
- timestamp invalid/regression;
- near-empty map after populated map;
- mass item disappearance;
- mass teamId=NONE;
- casualty counter regression;
- identity ambiguity spike;
- abrupt taxonomy/schema structural fingerprint change.

Each decision persists:

- rule key;
- rule version;
- configuration/threshold version;
- inputs;
- decision;
- evidence references.

Historical War API issue 92 is a required golden fixture: raw evidence must be retained, the representation must be quarantined by the frozen M6 policy, accepted canonical map state must not be replaced by mass NONE, and later change/identity stages must not consume it as an accepted observation.

M6 quality details, baseline selection, policy versioning and calibration are normative in [M6_MAPS_TAXONOMY_QUALITY.md](M6_MAPS_TAXONOMY_QUALITY.md).

## Coverage

Coverage answers whether Foxhole-Data had trustworthy observation capability over a time interval.

Implemented source-coverage states are:

- observed
- source_not_modified
- source_unavailable
- collector_unavailable
- rejected
- unknown

Quality rejection is separate from source coverage. A successfully captured/parsed representation can have source coverage `observed` while its M6 QualityRun is `suspect` or `quarantined`.

A coverage gap is never equivalent to no state change.

Historical API responses SHOULD be able to return coverage/quality metadata separately from the domain state.
