# ADR-0015 — M3 HTTP application-exchange boundary

Status: accepted.
Date: 2026-09-20.

## Context

M2 requires one durable ingestion attempt to perform at most one audited upstream exchange and forbids hidden retry/replay.

The default .NET 10 HTTP transport complicates a literal network-packet interpretation of that invariant:

- SocketsHttpHandler contains internal retry/recovery paths for selected connection failures and protocol fallback;
- AllowAutoRedirect defaults to enabled;
- Microsoft.Extensions.Http.Resilience standard pipelines include retry;
- automatic content decompression can transform response content before application code sees it.

FoxData needs an invariant that is both enforceable and compatible with the supported .NET transport.

## Decision

For source ingestion, one AttemptId authorizes at most one FoxData application-issued SendAsync operation.

FoxData MUST NOT perform a second SendAsync for the same AttemptId.

The War API client therefore has:

- no retry handler;
- no hedging;
- no AddStandardResilienceHandler;
- no manual send retry loop;
- AllowAutoRedirect=false;
- AutomaticDecompression=None.

Internal SocketsHttpHandler connection recovery or protocol fallback that happens inside that single application send is transport implementation behavior. It does not create another FoxData AttemptId because FoxData did not make another application-level send decision.

If FoxData elects to contact the source again, it creates a new AttemptId and obtains a new exchange authorization.

HTTP evidence payload identity is SHA-256 over the content bytes exposed by HttpClient after HTTP transfer framing is removed and before Content-Encoding decompression.

The payload hash does not cover TCP/TLS records, HTTP frame/chunk bytes or response headers.

## Consequences

Positive:

- the M2 invariant is enforceable in application code and tests;
- retries remain durable, observable orchestration decisions;
- redirects cannot silently escape the fixed source authority;
- content coding cannot silently change evidence bytes before hashing;
- normal HttpClient pooling/performance can still be used.

Cost:

- the invariant is not a claim that the runtime emitted exactly one physical wire request in every connection failure mode;
- compressed content, if received, requires separate bounded decoding after raw capture;
- standard convenience resilience pipelines cannot be attached without explicitly preserving this ADR.

## Rejected alternatives

### Literal one physical network request

Rejected because SocketsHttpHandler does not expose a supported switch that makes every internal connection recovery path equivalent to a guaranteed single physical request. Replacing the supported HTTP stack solely for that property is disproportionate to the source risk.

### Standard resilience handler with retries

Rejected because an in-process retry would make durable AttemptId accounting dishonest.

### Automatic redirects

Rejected because redirects can create extra requests and change authority without a new durable authorization.
