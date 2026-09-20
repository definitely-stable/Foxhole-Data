# Source adapters

The ingestion kernel MUST not contain War API-specific URL, DTO or parsing logic.

## Core concepts

Conceptual contracts:

~~~text
ISourceAdapter
  Describe()
  BuildPollPlan()
  BuildRequest()
  Parse()
  Fingerprint()
  Normalize()

ISourceCapability
  capability key
  source schema version
  freshness class
  expected change class
~~~

Initial source capabilities:

- RuntimeWarState
- ActiveMapList
- RegionWarReport
- StaticMapState
- DynamicMapState

Future optional capabilities:

- AssetCatalog
- MapMedia
- ItemCatalog
- TechTree
- ReferenceData

## M3 phase boundary

M3 implements source transport interpretation only:

~~~text
Describe
BuildPollPlan
BuildRequest
Parse
Fingerprint
~~~

Canonical Normalize remains an M5+ stage.

M3 source parsing may produce versioned parsed source results and diagnostics, but it MUST NOT write canonical runtime, quality or identity state.

The adapter does not own durable lease/fence correctness. It operates through Application contracts and M2 evidence boundaries.

## Adapter rules

- adapters use configured allowlisted source roots;
- production adapters never accept arbitrary user URLs;
- adapters do not own durable scheduling;
- adapters perform no hidden application retry or hedging;
- automatic redirects are disabled for fixed-root source ingestion;
- parser and later normalizer versions are persisted independently;
- exact raw content bytes remain available independently from parser success;
- additive unknown JSON properties are retained where practical;
- unknown enum/code values do not make raw capture impossible;
- semantic helpers that can change meaning have independent algorithm versions;
- source identifiers such as War API mapName remain exact/opaque unless a later semantic layer explicitly maps aliases;
- adapters do not write evidence/canonical tables directly.

## Representation state

Conditional source requests require a distinction between:

- latest current Fetch;
- latest successful validation Fetch;
- body-bearing reusable representation Fetch.

A 304 references the body-bearing representation Fetch, not merely the immediately previous Fetch.

M3 stores this scheduling/validator state outside M2 endpoint fence authority as defined by ADR-0016.

## Structural fingerprints

The adapter SHOULD compute a low-cost structural/schema fingerprint from successful raw payloads so contract drift can be observed separately from semantic data changes.

M3 uses versioned json-shape@1.

A structural fingerprint:

- does not replace raw payload SHA-256;
- ignores ordinary scalar value changes;
- is stable against object-property and array ordering where shape is unchanged;
- changes for additive/removal/type-shape drift;
- is diagnostic metadata and not automatic quality rejection.

## Source parse runs

Parsing is versioned derived evidence.

M3 persists source parse runs over body-bearing representations so:

- parser upgrades can reprocess the same raw bytes;
- a crash after raw capture does not cause another source request;
- unknown/additive fields can be counted and inspected;
- 304 validations do not create duplicate parse work solely because the source was revalidated.
