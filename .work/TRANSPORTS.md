# Transport architecture

Foxhole-Data is transport-neutral above Application handlers.

## v1 transport set

Mandatory:

1. HTTPS REST/JSON — public remote query/control API.
2. stdio JSON-RPC 2.0 — local subprocess RPC.
3. SSE — remote low-latency change delivery layered on the durable change feed.
4. HTTPS webhooks — outbound push.
5. file/object outputs — NDJSON, CSV and Parquet exports.

Optional after measured need:

- gRPC;
- HTTP/gRPC over Unix domain sockets;
- HTTP/gRPC over Windows Named Pipes;
- message broker transports.

## Why HTTPS remains the public reference transport

HTTPS is universally accessible from browsers, scripts and services, supports standard authentication, caching, conditional requests, reverse proxies, CDNs and observability, and is described by OpenAPI.

It is the reference public transport, not the application architecture.

## Why stdio is first-class

stdio is ideal for local tools, agents, CLI orchestration, test harnesses and applications that want a subprocess without opening a port.

The proven current pattern used by modern tool protocols is:

- client launches subprocess;
- protocol messages on stdin/stdout;
- diagnostics on stderr;
- stdout contains protocol messages only;
- protocol semantics are independent from transport.

Foxhole-Data adopts this transport pattern but defines its own domain RPC contract.

## Local sockets/pipes

.NET 10 supports gRPC/HTTP IPC over Unix domain sockets and Windows Named Pipes. These can improve same-machine IPC and OS-level access control, but they make cross-language installation more complex and duplicate deployment modes.

They are therefore extension transports, not mandatory v1.

## Semantic equivalence rule

These operations must have the same domain meaning regardless of transport:

~~~text
HTTP GET current war
stdio foxdata.war.current
future gRPC GetCurrentWar
~~~

Transport adapters may differ in framing, authentication and streaming mechanics, but not domain interpretation.
