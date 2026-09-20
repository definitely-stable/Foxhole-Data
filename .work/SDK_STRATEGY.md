# SDK strategy

The server language is an implementation detail.

No consumer may be required to reference a Foxhole-Data .NET assembly.

## Two-layer SDK model

Layer A — generated transport SDK:

- generated from released OpenAPI;
- HTTP transport only;
- typed resources and errors;
- status and response header access;
- cancellation/abort;
- pagination primitives;
- authentication;
- raw-response escape hatch.

Layer B — hand-written semantic/helper SDK:

- depends on public contract models, never server internals;
- map aliases;
- coordinate helpers;
- flag/icon utilities;
- pagination iterators;
- SSE reconnect helper;
- webhook verification;
- local cache helpers;
- optional rendering utilities.

Generated code and hand-written code MUST live in separate directories/packages so regeneration never overwrites semantic helpers.

## Initial target languages

First wave:

- TypeScript
- Python
- C#
- Rust

Second wave based on demand:

- Go
- Java/Kotlin
- Swift
- PHP/Ruby

## Generator selection

Do not require one generator for every language.

Kiota is a strong candidate for C#, Go, Java, PHP, Python, Ruby and TypeScript.

OpenAPI Generator covers a broader matrix including Rust and Swift, but generator maturity differs by target.

Each official SDK target MUST pass a language-specific compatibility and ergonomics test suite before release.

## SDK requirements

Where idiomatic:

- async-first API;
- cancellation support;
- Retry-After handling;
- no unsafe hidden retry of non-idempotent operations;
- cursor iterator;
- open-enum tolerance;
- SSE reconnect and cursor recovery;
- Standard Webhooks verification helper;
- SemVer;
- generated-code provenance including API contract revision.

Python MAY add a sync convenience facade over an async core.

## stdio client libraries

stdio RPC is simple enough that language-specific clients MAY be hand-written or generated from a small protocol descriptor later. It MUST not block HTTP SDK availability.
