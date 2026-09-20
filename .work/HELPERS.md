# Developer helpers

Community wrappers repeatedly implement the same semantics. Foxhole-Data should standardize the parts that should be identical for all consumers while leaving presentation-specific behavior in SDKs.

## Server-side canonical helpers

Expose deterministic versioned server semantics for:

- aggregate casualties;
- scorched-aware/effective victory-town requirements;
- victory-town ownership;
- current war summary;
- state-at-time reconstruction;
- objective ownership history;
- map-name alias resolution;
- icon taxonomy lookup;
- documented flag decoding;
- coverage and quality summaries.

## Client/SDK helpers

Keep consumer-local behavior in helper SDKs:

- coordinate distance;
- world/normalized coordinate transforms;
- rendering primitives;
- image composition;
- batching/concurrency helpers;
- local conditional GET cache;
- display formatting;
- map-item/text association policies when more than one legitimate strategy exists.

## Helper contract

Any semantic helper whose interpretation may change MUST document:

- inputs;
- output;
- algorithm version;
- taxonomy version where relevant;
- behavior on unknown source values;
- whether the result is source fact, normalized fact or derived fact.

Do not call an observed owner transition a capture unless a source supplies capture semantics.
