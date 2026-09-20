# Contract governance

Foxhole-Data is a developer platform; contracts are release artifacts, not generated documentation afterthoughts.

## Sources of truth

HTTP:

- .work/contracts/openapi/public-v1.yaml
- .work/contracts/openapi/compat-warapi-v1.yaml

Events:

- .work/contracts/asyncapi/events-v1.yaml
- payload schemas referenced by the event contract

stdio:

- .work/contracts/jsonrpc/stdio-v1.md

Architecture prose explains semantics. Executable contracts define wire compatibility.

## Design-first workflow

1. propose semantic/API change;
2. update the contract;
3. review naming, compatibility, pagination, errors, cache semantics and examples;
4. implement;
5. generate ASP.NET OpenAPI at build time;
6. compare generated implementation document with the canonical contract;
7. run breaking-change gates against the latest released contract;
8. generate/test SDKs.

There MUST NOT be two independently edited public OpenAPI truths.

## CI gates

At minimum:

- OpenAPI lint and reference resolution;
- AsyncAPI validation;
- JSON Schema validation;
- canonical-vs-implementation OpenAPI conformance;
- OpenAPI breaking-change check;
- event schema compatibility check;
- SDK generation smoke tests;
- example requests validated against schemas.

oasdiff is the initial recommended OpenAPI compatibility checker. Redocly CLI is the initial recommended OpenAPI lint/bundle tool. AsyncAPI CLI validates event contracts.

## OpenAPI version policy

The runtime baseline is .NET 10, therefore the canonical executable contract remains OpenAPI 3.1.x.

OpenAPI 3.2.1 is the current published specification and the upgrade target. Upgrade only when the implementation and chosen toolchain support it end-to-end without lossy conversion.

## Semantic versioning

Independently version:

- public HTTP API major;
- stdio RPC protocol;
- event payload schema/type;
- taxonomy;
- source parser;
- source normalizer;
- objective matcher;
- change detector;
- collection profile;
- export dataset schema.
