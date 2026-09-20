# Quality and coverage

Raw collection success is not the same thing as canonical acceptance.

Pipeline:

~~~text
raw durable
   |
parsed
   |
quality gate
   +-- accepted
   +-- suspect
   +-- quarantined
   +-- schema_rejected
   |
canonical reconcile only when policy allows
~~~

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

Historical War API issue 92 is a required golden fixture: raw evidence must be retained, state must be marked suspect/quarantined, accepted canonical state must not be replaced by mass NONE, and mass false owner changes must not be emitted.

## Coverage

Coverage answers whether Foxhole-Data had trustworthy observation capability over a time interval.

Coverage states MAY include:

- observed
- source_not_modified
- source_unavailable
- collector_unavailable
- rejected_quality
- unknown

A coverage gap is never equivalent to no state change.

Historical API responses SHOULD be able to return coverage/quality metadata separately from the domain state.
