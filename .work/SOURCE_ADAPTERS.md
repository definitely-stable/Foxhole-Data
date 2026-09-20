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

## Adapter rules

- adapters use configured allowlisted source roots;
- adapters never accept arbitrary user URLs;
- adapters do not own durable scheduling;
- adapters perform no hidden retry;
- parser and normalizer versions are persisted;
- exact raw bytes remain available independently from parser success;
- additive unknown JSON properties are retained where practical;
- unknown enum/code values do not make raw capture impossible;
- semantic helpers that can change meaning have independent algorithm versions.

## Structural fingerprints

The adapter SHOULD compute a low-cost structural/schema fingerprint from successful raw payloads so contract drift can be observed separately from semantic data changes.

A structural fingerprint is diagnostic metadata and does not replace raw payload hashes.
